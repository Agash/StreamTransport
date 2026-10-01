using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using Agash.StreamTransport.Threading;
using Agash.StreamTransport.WebRtc.Stun;
using Agash.StreamTransport.WebRtc.Turn;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.WebRtc.Ice;

/// <summary>
/// A full-ICE agent (RFC 8445) over UDP with trickle (RFC 8838): it gathers host candidates (one socket
/// per local address, so the source address is pinned) and relayed candidates from TURN servers
/// (RFC 8656, reached over UDP, TCP or TLS), runs STUN connectivity checks against trickled remote
/// candidates, nominates, and keeps consent (RFC 7675) on the selected pair and warm alternates.
/// Non-STUN datagrams on its sockets (DTLS, SRTP) are surfaced through <see cref="DataReceived"/>;
/// outbound media goes through <see cref="SendAsync"/>.
/// </summary>
/// <remarks>
/// The protocol is an <see cref="IceStateMachine"/>; this drives it: it owns the sockets and their
/// receive loops, hands the machine datagrams and the time, sends what it asks, raises its events, and
/// sleeps on the injected clock until the machine next needs the time.
/// </remarks>
public sealed partial class IceAgent : IAsyncDisposable
{
    private readonly IceStateMachine _machine;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly long _origin;
    private readonly bool _includeLoopback;
    private readonly IIceSocketFactory _socketFactory;
    private readonly IceTransportPolicy _policy;
    private readonly List<TurnServer> _turnServers = [];
    private readonly WakeSignal _wake;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, LocalSocket> _sockets = [];
    private CancellationTokenSource? _cts;
    private Task? _timerLoop;
    private int _nextHandle;
    private int _generation;
    private int _pendingRelays;
    private TaskCompletionSource _gathered = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private int _state = (int)IceConnectionState.New;

    // The selected pair as one object, so a reader never sees one selection's socket with another's peer.
    private Selection? _selection;
    private readonly WebRtcMetrics _metrics;

    // Every socket has its own receive loop, and the old path and a warm standby both carry packets
    // around a switch; handlers (SRTP replay windows, RTCP, depacketizers) see one packet at a time.
    private readonly Lock _delivery = new();

    /// <summary>
    /// Creates an agent. <paramref name="role"/> follows the offer/answer (offerer = controlling).
    /// <paramref name="socketFactory"/> defaults to real UDP; tests inject an in-memory one.
    /// </summary>
    /// <param name="localCredentials">This agent's ufrag and password.</param>
    /// <param name="role">Controlling or controlled.</param>
    /// <param name="includeLoopback">Whether to gather loopback candidates.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="socketFactory">Creates the sockets candidates are gathered on.</param>
    /// <param name="timings">Check pacing, retransmission and consent timing.</param>
    /// <param name="timeProvider">The clock that paces checks and measures consent; the system's when null.</param>
    /// <param name="transportPolicy">Which candidates to gather.</param>
    /// <param name="meterFactory">Where the metrics' meter comes from (<see cref="WebRtcDiagnostics.MeterName"/>); one shared meter when null.</param>
    public IceAgent(
        IceCredentials localCredentials,
        IceRole role,
        bool includeLoopback = false,
        ILogger<IceAgent>? logger = null,
        IIceSocketFactory? socketFactory = null,
        IceTimings? timings = null,
        TimeProvider? timeProvider = null,
        IceTransportPolicy transportPolicy = IceTransportPolicy.All,
        IMeterFactory? meterFactory = null
    )
    {
        _policy = transportPolicy;
        _metrics = WebRtcMetrics.For(meterFactory);
        _time = timeProvider ?? TimeProvider.System;
        _origin = _time.GetTimestamp();
        _includeLoopback = includeLoopback;
        _socketFactory = socketFactory ?? new UdpIceSocketFactory();
        _logger = logger ?? NullLogger<IceAgent>.Instance;
        _machine = new IceStateMachine(
            localCredentials,
            role,
            timings ?? IceTimings.Default,
            _logger,
            _metrics
        );
        _wake = new WakeSignal(_time, () => Now);
    }

    /// <summary>Raised once per gathered local candidate (trickle these to the peer).</summary>
    public event Action<IceCandidate>? LocalCandidateGathered;

    /// <summary>Raised when the connection state changes.</summary>
    public event Action<IceConnectionState>? StateChanged;

    /// <summary>
    /// Raised for every non-STUN datagram received (DTLS / SRTP), with the source endpoint and the packet's
    /// 2-bit ECN mark (0 when the platform does not surface it). The buffer is the agent's reused receive
    /// buffer, borrowed only for the synchronous duration of the handler: the handler may read and mutate it in
    /// place (SRTP decrypts into it) but must copy anything it keeps beyond the call. It is raised for one
    /// packet at a time across all of the agent's sockets.
    /// </summary>
    public event Action<Memory<byte>, IPEndPoint, byte>? DataReceived;

    /// <summary>The current connection state.</summary>
    public IceConnectionState State => (IceConnectionState)Volatile.Read(ref _state);

    /// <summary>The selected candidate pair's local and remote endpoints, or null while none is selected.</summary>
    public IcePath? SelectedPath =>
        Volatile.Read(ref _selection) is { } selection
            ? new IcePath(
                selection.Socket.Socket.LocalEndPoint,
                selection.Remote,
                selection.LocalKind,
                selection.RemoteKind
            )
            : null;

    /// <summary>This agent's local credentials (rotated on an ICE restart).</summary>
    public IceCredentials LocalCredentials
    {
        get
        {
            lock (_gate)
            {
                return _machine.LocalCredentials;
            }
        }
    }

    // Monotonic time since the agent was created; wall-clock steps do not move it.
    private TimeSpan Now => _time.GetElapsedTime(_origin);

    /// <summary>Sets the remote agent's credentials (from the peer's SDP). Required before checks can pass.</summary>
    /// <param name="remote">The remote credentials.</param>
    public void SetRemoteCredentials(IceCredentials remote)
    {
        lock (_gate)
        {
            _machine.SetRemoteCredentials(remote);
        }
    }

    /// <summary>
    /// Adds a STUN server to query for server-reflexive candidates (the public mapping behind a NAT).
    /// Call before <see cref="Start"/>.
    /// </summary>
    /// <param name="server">The server.</param>
    public void AddStunServer(IPEndPoint server)
    {
        lock (_gate)
        {
            _machine.AddStunServer(server);
        }
    }

    /// <summary>
    /// Adds a TURN server to allocate a relayed candidate on, one per address family the server
    /// resolves to. Call before <see cref="Start"/>.
    /// </summary>
    /// <param name="server">The server and its credentials.</param>
    public void AddTurnServer(TurnServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        lock (_gate)
        {
            _turnServers.Add(server);
        }
    }

    /// <summary>
    /// Binds a UDP socket per local address and raises <see cref="LocalCandidateGathered"/> for each host
    /// candidate, then starts checking. Relayed candidates follow as their allocations complete. Call once.
    /// </summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        lock (_gate)
        {
            Gather(_cts.Token);
            _machine.Start(Now);
        }

        Report();
        _timerLoop = TimerLoopAsync(_cts.Token);
        StartRelays(_cts.Token);
    }

    /// <summary>Adds a remote candidate learned via signaling (trickle ICE).</summary>
    /// <param name="candidate">The candidate.</param>
    public void AddRemoteCandidate(IceCandidate candidate)
    {
        lock (_gate)
        {
            _machine.AddRemoteCandidate(candidate);
        }
    }

    /// <summary>
    /// Waits until every candidate the agent gathers has been raised: host candidates, server-reflexive
    /// ones from the STUN servers that answer, relayed ones from the TURN servers that allocate. A peer
    /// that does not trickle (WHIP, WHEP) needs them all in one description. Waits at most
    /// <paramref name="limit"/>; what has been gathered by then is what there is.
    /// </summary>
    /// <param name="limit">The longest to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when gathering finished or the limit passed.</returns>
    public async Task WhenGatheredAsync(
        TimeSpan limit,
        CancellationToken cancellationToken = default
    )
    {
        Task gathered;
        lock (_gate)
        {
            gathered = _gathered.Task;
        }

        try
        {
            await gathered.WaitAsync(limit, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Deliberately not logged: a server that does not answer leaves gathering at what it found.
        }
    }

    /// <summary>
    /// Sends a media or DTLS datagram over the selected pair. While no pair is selected (during a
    /// recovery) the datagram is dropped: it would be lost on the dead path anyway, and the media pump
    /// must not fault while ICE re-nominates.
    /// </summary>
    /// <param name="data">The datagram.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the datagram is handed to the socket.</returns>
    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default
    )
    {
        if (Volatile.Read(ref _selection) is not { } selection)
        {
            return;
        }

        await selection
            .Socket.Socket.SendAsync(data, selection.Remote, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Re-probes all candidate pairs and re-nominates, on consent loss or a network change, so the agent
    /// fails over to whatever path now works. The DTLS-SRTP session above is bound to the peer's
    /// certificate, so its keys and rollover counter carry across the switch.
    /// </summary>
    public void TriggerRecovery()
    {
        lock (_gate)
        {
            _machine.TriggerRecovery();
        }

        Report();
        _wake.Signal();
    }

    /// <summary>
    /// Full ICE restart (RFC 8445 section 9): fresh local credentials, freshly gathered host candidates
    /// (trickled again), and new checks under the new credentials. The DTLS-SRTP session carries on.
    /// </summary>
    /// <param name="newLocalCredentials">The new credentials.</param>
    public void Restart(IceCredentials newLocalCredentials)
    {
        lock (_gate)
        {
            foreach (LocalSocket socket in _sockets.Values)
            {
                socket.Socket.Dispose();
            }

            _sockets.Clear();
            _generation++;
            _machine.Restart(newLocalCredentials);
            Gather(_cts?.Token ?? CancellationToken.None);
        }

        lock (_gate)
        {
            if (_gathered.Task.IsCompleted)
            {
                _gathered = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
            }
        }

        Report();
        StartRelays(_cts?.Token ?? CancellationToken.None);
        _wake.Signal();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // Idempotent: callers commonly stop and then dispose, which routes here twice.
        CancellationTokenSource? cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);
        if (_timerLoop is { } loop)
        {
            await loop.ConfigureAwait(false);
        }

        lock (_gate)
        {
            foreach (LocalSocket socket in _sockets.Values)
            {
                socket.Socket.Dispose();
            }

            _sockets.Clear();
        }

        cts.Dispose();
    }

    // Binds a socket per usable local address and hands each to the machine. Called holding the lock.
    private void Gather(CancellationToken cancellationToken)
    {
        if (_policy == IceTransportPolicy.Relay)
        {
            return;
        }

        foreach (IPAddress address in _socketFactory.GetLocalAddresses(_includeLoopback))
        {
            if (!_socketFactory.TryBind(address, out IIceSocket socket))
            {
                continue; // The family is unavailable or the address is not bindable.
            }

            int handle = _nextHandle++;
            LocalSocket local = new(handle, socket);
            _sockets[handle] = local;
            _machine.AddLocalEndpoint(handle, socket.LocalEndPoint);
            local.Receiving = ReceiveLoopAsync(local, cancellationToken);
        }
    }

    // Allocates on every TURN server in the background; each relay joins as a local endpoint when ready.
    private void StartRelays(CancellationToken cancellationToken)
    {
        int generation;
        TurnServer[] servers;
        lock (_gate)
        {
            generation = _generation;
            servers = [.. _turnServers];
        }

        foreach (TurnServer server in servers)
        {
            _ = Interlocked.Increment(ref _pendingRelays);
            _ = TrackAsync(AllocateRelaysAsync(server, generation, cancellationToken));
        }

        CheckGathered();
    }

    private async Task TrackAsync(Task allocation)
    {
        try
        {
            await allocation.ConfigureAwait(false);
        }
        finally
        {
            _ = Interlocked.Decrement(ref _pendingRelays);
            CheckGathered();
        }
    }

    // Gathering is complete when no reflexive query or relay allocation is outstanding.
    private void CheckGathered()
    {
        lock (_gate)
        {
            if (_machine.PendingGathers == 0 && Volatile.Read(ref _pendingRelays) == 0)
            {
                _ = _gathered.TrySetResult();
            }
        }
    }

    // One allocation per address family the server resolves to, IPv6 first.
    private async Task AllocateRelaysAsync(
        TurnServer server,
        int generation,
        CancellationToken cancellationToken
    )
    {
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(server.Host, out IPAddress? literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(server.Host, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (SocketException exception)
        {
            LogTurnUnresolved(exception, server.Host);
            return;
        }
        catch (OperationCanceledException)
        {
            // Deliberately not logged: the agent stopped while resolving.
            return;
        }

        IEnumerable<IPAddress> perFamily = addresses
            .Where(static a =>
                a.AddressFamily is AddressFamily.InterNetworkV6 or AddressFamily.InterNetwork
            )
            .GroupBy(static a => a.AddressFamily)
            .OrderByDescending(static g => g.Key == AddressFamily.InterNetworkV6)
            .Select(static g => g.First());
        await Task.WhenAll(
                perFamily.Select(address =>
                    AllocateRelayAsync(
                        server,
                        new IPEndPoint(address, server.Port),
                        generation,
                        cancellationToken
                    )
                )
            )
            .ConfigureAwait(false);
    }

    private async Task AllocateRelayAsync(
        TurnServer server,
        IPEndPoint endpoint,
        int generation,
        CancellationToken cancellationToken
    )
    {
        TurnAllocation allocation;
        try
        {
            allocation = await TurnAllocation
                .AllocateAsync(server, endpoint, _time, _logger, cancellationToken)
                .ConfigureAwait(false);
            _metrics.TurnAllocations.Add(
                1,
                WebRtcMetrics.Transport(TransportName(server.Transport)),
                WebRtcMetrics.Outcome("allocated")
            );
        }
        catch (OperationCanceledException)
        {
            // Deliberately not logged: the agent stopped while allocating.
            return;
        }
        catch (Exception exception)
            when (exception
                    is TurnException
                        or SocketException
                        or IOException
                        or System.Security.Authentication.AuthenticationException
            )
        {
            _metrics.TurnAllocations.Add(
                1,
                WebRtcMetrics.Transport(TransportName(server.Transport)),
                WebRtcMetrics.Outcome(exception is TurnException ? "refused" : "unreachable")
            );
            LogTurnFailed(exception, server.Host, endpoint);
            return;
        }

        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested || generation != _generation)
            {
                allocation.Dispose();
                return;
            }

            int handle = _nextHandle++;
            LocalSocket local = new(handle, allocation);
            _sockets[handle] = local;
            _machine.AddLocalEndpoint(
                handle,
                allocation.LocalEndPoint,
                IceCandidateKind.Relayed,
                allocation.MappedAddress
            );
            local.Receiving = ReceiveLoopAsync(local, cancellationToken);
        }

        Report();
        _wake.Signal();
    }

    // Sends what the machine asks, then sleeps until it next needs the time or until a call from outside
    // leaves it something to send.
    private async Task TimerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                TimeSpan? deadline;
                lock (_gate)
                {
                    _machine.HandleTimeout(Now);
                    deadline = _machine.NextTimeout;
                }

                Report();
                await SendPendingAsync(cancellationToken).ConfigureAwait(false);
                if (deadline is { } next)
                {
                    _ = await _wake.WaitUntilAsync(next, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Deliberately not logged: cancellation is how the agent stops.
        }
    }

    private async Task ReceiveLoopAsync(LocalSocket local, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[2048];
        while (!cancellationToken.IsCancellationRequested)
        {
            IceReceiveResult result;
            try
            {
                result = await local
                    .Socket.ReceiveAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Deliberately not logged: cancellation is how the agent stops receiving.
                return;
            }
            catch (ObjectDisposedException)
            {
                // Deliberately not logged: a restart closed this socket.
                return;
            }
            catch (SocketException)
            {
                // Deliberately not logged: an ICMP port-unreachable from a check to a dead candidate
                // surfaces here, once per such check.
                continue;
            }
            catch (IOException exception)
            {
                // A TURN server's TCP or TLS connection closed; the relay's pairs fail over by consent.
                LogSocketClosed(exception, local.Socket.LocalEndPoint);
                return;
            }

            if (
                StunMessageReader.TryParse(
                    buffer.AsSpan(0, result.Length),
                    out StunMessageReader stun
                )
            )
            {
                lock (_gate)
                {
                    _machine.HandleStun(local.Handle, result.RemoteEndPoint, stun, Now);
                }

                Report();
                CheckGathered();
                await SendPendingAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // DTLS or SRTP, in the reused buffer: the handler runs before the next receive. A handler
                // that throws loses its packet, never the socket: an ended loop leaves the path deaf to
                // consent while sending carries on, until ICE declares it dead.
                try
                {
                    lock (_delivery)
                    {
                        DataReceived?.Invoke(
                            buffer.AsMemory(0, result.Length),
                            result.RemoteEndPoint,
                            result.Ecn
                        );
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    LogDataHandlerFailed(exception, result.RemoteEndPoint);
                }
            }
        }
    }

    // Raises what the machine reported and publishes its selection, outside the lock.
    private void Report()
    {
        List<IceEvent> events = [];
        lock (_gate)
        {
            while (_machine.TryPollEvent(out IceEvent iceEvent))
            {
                events.Add(iceEvent);
            }

            Volatile.Write(
                ref _selection,
                _machine.Selected is { } pair
                && _sockets.TryGetValue(pair.Local, out LocalSocket? chosen)
                    ? new Selection(chosen, pair.Remote, pair.LocalKind, pair.RemoteKind)
                    : null
            );
        }

        foreach (IceEvent iceEvent in events)
        {
            // A subscriber that throws is logged; the loop that reported keeps running.
            try
            {
                if (iceEvent.Candidate is { } candidate)
                {
                    LocalCandidateGathered?.Invoke(candidate);
                }
                else if (
                    Interlocked.Exchange(ref _state, (int)iceEvent.State) != (int)iceEvent.State
                )
                {
                    StateChanged?.Invoke(iceEvent.State);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogEventHandlerFailed(exception);
            }
        }
    }

    // Sends what the machine asked to send.
    private async ValueTask SendPendingAsync(CancellationToken cancellationToken)
    {
        List<(IIceSocket Socket, IceTransmit Transmit)> transmits = [];
        lock (_gate)
        {
            while (_machine.TryPollTransmit(out IceTransmit transmit))
            {
                if (_sockets.TryGetValue(transmit.Local, out LocalSocket? socket))
                {
                    transmits.Add((socket.Socket, transmit));
                }
            }
        }

        foreach ((IIceSocket socket, IceTransmit transmit) in transmits)
        {
            try
            {
                await socket
                    .SendAsync(transmit.Data, transmit.Destination, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SocketException exception)
            {
                // A check to an unreachable candidate; the machine retransmits or gives up on the pair.
                LogSendFailed(exception, transmit.Destination);
            }
            catch (ObjectDisposedException)
            {
                // Deliberately not logged: a restart closed the socket, and its pairs went with it.
            }
        }
    }

    [LoggerMessage(
        EventId = 1110,
        Level = LogLevel.Debug,
        Message = "ICE send to {Destination} failed"
    )]
    private partial void LogSendFailed(Exception exception, IPEndPoint destination);

    [LoggerMessage(
        EventId = 1111,
        Level = LogLevel.Error,
        Message = "A packet from {Source} failed in its handler; it is dropped and receiving goes on"
    )]
    private partial void LogDataHandlerFailed(Exception exception, IPEndPoint source);

    [LoggerMessage(
        EventId = 1112,
        Level = LogLevel.Warning,
        Message = "TURN server {Server} did not resolve"
    )]
    private partial void LogTurnUnresolved(Exception exception, string server);

    [LoggerMessage(
        EventId = 1113,
        Level = LogLevel.Warning,
        Message = "TURN allocation on {Server} at {Endpoint} failed; no relayed candidate from it"
    )]
    private partial void LogTurnFailed(Exception exception, string server, IPEndPoint endpoint);

    [LoggerMessage(
        EventId = 1114,
        Level = LogLevel.Warning,
        Message = "The connection behind {Local} closed"
    )]
    private partial void LogSocketClosed(Exception exception, IPEndPoint local);

    [LoggerMessage(EventId = 1115, Level = LogLevel.Error, Message = "An ICE event handler failed")]
    private partial void LogEventHandlerFailed(Exception exception);

    private sealed class LocalSocket(int handle, IIceSocket socket)
    {
        public int Handle { get; } = handle;

        public IIceSocket Socket { get; } = socket;

        public Task? Receiving { get; set; }
    }

    private static string TransportName(TurnTransport transport) =>
        transport switch
        {
            TurnTransport.Udp => "udp",
            TurnTransport.Tcp => "tcp",
            _ => "tls",
        };

    private sealed record Selection(
        LocalSocket Socket,
        IPEndPoint Remote,
        IceCandidateKind LocalKind,
        IceCandidateKind RemoteKind
    );
}
