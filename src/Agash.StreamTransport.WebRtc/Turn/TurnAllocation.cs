using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.Stun;
using Agash.StreamTransport.WebRtc.Ice;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.WebRtc.Turn;

/// <summary>
/// A TURN allocation (RFC 8656): a UDP relay address on a TURN server, used by ICE as a local socket.
/// A peer's IP address gets a permission (CreatePermission) before anything is relayed to it; data to a
/// peer then goes through a channel once one is bound (four octets of overhead) and as a Send indication
/// until then. What peers send comes back unwrapped, with the peer as the source. The allocation, its
/// permissions and its channels are refreshed on the injected clock.
/// </summary>
/// <remarks>
/// After <see cref="AllocateAsync"/> returns, the ICE receive loop drives the connection through
/// <see cref="ReceiveAsync"/>: it returns peer data and completes the allocation's own transactions on
/// the way, so no second reader competes for the connection. When the server reports the allocation
/// gone, <see cref="ReceiveAsync"/> throws <see cref="IOException"/> and sends are dropped, so the
/// relay's pairs fail as a closed TCP or TLS connection's do.
/// </remarks>
internal sealed partial class TurnAllocation : IIceSocket
{
    private const uint RequestedLifetime = 600;
    private const ushort FirstChannel = 0x4000;
    private const ushort LastChannel = 0x4FFF;

    // RFC 8489 section 6.2.1: Rc transmissions over UDP, the last one waited on for Rm times the
    // initial RTO; over TCP and TLS one transmission and a 39.5 s timeout (Ti).
    private const int UdpTransmissions = 7;
    private const int LastWaitFactor = 16;
    private static readonly TimeSpan InitialRto = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReliableTimeout = TimeSpan.FromSeconds(39.5);

    // RFC 8656 section 9: permissions last 300 s. They and channels (10 minutes, section 12) are
    // refreshed a minute before the permission would lapse, as libwebrtc does.
    private static readonly TimeSpan PermissionRefresh = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan FailureRetry = TimeSpan.FromSeconds(5);

    // Datagrams waiting for their peer's permission; checks retransmit, so a few suffice.
    private const int MaxQueuedPerPermission = 8;

    private static readonly byte[] s_software = Encoding.UTF8.GetBytes(
        "Agash.StreamTransport "
            + (
                typeof(TurnAllocation)
                    .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion.Split('+')[0]
                ?? "0"
            )
    );

    private readonly TurnServer _server;
    private readonly TurnConnection _connection;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly long _origin;
    private readonly Lock _gate = new();
    private readonly Dictionary<IPEndPoint, Peer> _peers = [];
    private readonly Dictionary<ushort, IPEndPoint> _channels = [];
    private readonly Dictionary<IPAddress, Permission> _permissions = [];
    private readonly Dictionary<UInt128, TaskCompletionSource<byte[]>> _pending = [];
    private readonly CancellationTokenSource _lifetime = new();
    private ITimer? _maintenance;
    private byte[] _key = [];
    private byte[] _realm = [];
    private byte[] _nonce = [];
    private ushort _nextChannel = FirstChannel;
    private TimeSpan _expires;
    private TimeSpan _refreshAt;
    private string? _gone;
    private int _maintaining;
    private int _disposed;

    private TurnAllocation(
        TurnServer server,
        TurnConnection connection,
        TimeProvider time,
        ILogger logger
    )
    {
        _server = server;
        _connection = connection;
        _time = time;
        _logger = logger;
        _origin = time.GetTimestamp();
        LocalEndPoint = new IPEndPoint(IPAddress.None, 0);
        MappedAddress = LocalEndPoint;
    }

    /// <summary>The relayed transport address peers send to.</summary>
    public IPEndPoint LocalEndPoint { get; private set; }

    /// <summary>The client's address as the server saw it, the relayed candidate's related address.</summary>
    public IPEndPoint MappedAddress { get; private set; }

    private TimeSpan Now => _time.GetElapsedTime(_origin);

    /// <summary>
    /// Connects to <paramref name="server"/> at <paramref name="endpoint"/> and allocates a relay of the
    /// endpoint's address family, answering the server's long-term credential challenge. A 437 is
    /// retried from two more client ports and a 300 follows the server's ALTERNATE-SERVER once
    /// (RFC 8656 section 7.4, RFC 8489 section 10).
    /// </summary>
    /// <param name="server">The server and credentials.</param>
    /// <param name="endpoint">The server address to use.</param>
    /// <param name="time">The clock for retransmissions and refreshes.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">Cancels the allocation.</param>
    /// <returns>The allocation.</returns>
    /// <exception cref="TurnException">The server refused the allocation.</exception>
    public static async Task<TurnAllocation> AllocateAsync(
        TurnServer server,
        IPEndPoint endpoint,
        TimeProvider time,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        HashSet<IPEndPoint> redirected = [endpoint];
        int mismatches = 0;
        while (true)
        {
            TurnConnection connection = await TurnConnection
                .ConnectAsync(server, endpoint, cancellationToken)
                .ConfigureAwait(false);
            var allocation = new TurnAllocation(server, connection, time, logger);
            try
            {
                await allocation
                    .RequestAllocationAsync(endpoint.AddressFamily, cancellationToken)
                    .ConfigureAwait(false);
                allocation._maintenance = time.CreateTimer(
                    static state => ((TurnAllocation)state!).Maintain(),
                    allocation,
                    allocation.NextDue(),
                    Timeout.InfiniteTimeSpan
                );
                return allocation;
            }
            catch (TurnException exception) when (exception.Code == 437 && ++mismatches < 3)
            {
                // RFC 8656 section 7.4: the server sees this 5-tuple in use; try another client port.
                allocation.LogAllocationMismatch(server.Host, mismatches);
                allocation.Dispose();
            }
            catch (TurnException exception)
                when (exception.AlternateServer is { } alternate && redirected.Add(alternate))
            {
                // RFC 8489 section 10: the same request and credentials, to the alternate server.
                allocation.LogRedirected(server.Host, alternate);
                allocation.Dispose();
                endpoint = alternate;
            }
            catch
            {
                allocation.Dispose();
                throw;
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> data,
        IPEndPoint destination,
        IpMarking marking,
        CancellationToken cancellationToken = default
    )
    {
        Peer peer;
        bool bind;
        bool request = false;
        lock (_gate)
        {
            // A relay that is gone, or a peer of the other address family (RFC 8656 section 10.1),
            // loses the datagram as a network would; ICE's checks and consent notice.
            if (_gone is not null || destination.AddressFamily != LocalEndPoint.AddressFamily)
            {
                return;
            }

            if (
                !_permissions.TryGetValue(destination.Address, out Permission? permission)
                || permission.State != PermissionState.Installed
            )
            {
                // RFC 8656 section 11.1: nothing is relayed to a peer without a permission. The
                // datagram waits for it, and the first one asks for it.
                if (permission is null || permission.State == PermissionState.Failed)
                {
                    if (permission is not null && Now < permission.RetryAt)
                    {
                        return;
                    }

                    permission = new Permission();
                    _permissions[destination.Address] = permission;
                    request = true;
                }

                if (permission.Queue.Count == MaxQueuedPerPermission)
                {
                    _ = permission.Queue.Dequeue();
                }

                permission.Queue.Enqueue((destination, data.ToArray()));
                if (request)
                {
                    _ = RequestPermissionAsync(destination.Address);
                }

                return;
            }

            peer = PeerFor(destination);
            bind = peer.Channel != 0 && !peer.Binding && !peer.Bound && Now >= peer.RetryAt;
            peer.Binding |= bind;
        }

        if (bind)
        {
            _ = BindAsync(peer);
        }

        await RelayAsync(peer, data, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<IceReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            int length;
            try
            {
                ThrowIfGone();
                length = await _connection
                    .ReceiveAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
                when (Volatile.Read(ref _gone) is not null
                    && exception is ObjectDisposedException or SocketException
                )
            {
                // Deliberately not logged here: Fail logged why the connection was closed.
                ThrowIfGone();
                throw;
            }

            if (Unwrap(buffer.Span[..length], out int dataLength) is { } source)
            {
                return new IceReceiveResult(dataLength, source);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _maintenance?.Dispose();
        _lifetime.Cancel();
        if (Volatile.Read(ref _gone) is null && _key.Length > 0)
        {
            try
            {
                // Releases the relay now instead of when its lifetime runs out. A 437 answer means it
                // was already gone, which is as good (RFC 8656 section 8.3), so nothing waits for one.
                byte[] buffer = new byte[1024];
                int length = WriteRequest(
                    buffer,
                    StunMethod.Refresh,
                    NewTransaction(),
                    static (ref w) => w.AddUInt32(StunAttributeType.Lifetime, 0)
                );
                _connection.SendFinal(buffer.AsSpan(0, length));
            }
            catch (Exception exception)
                when (exception is IOException or SocketException or ObjectDisposedException)
            {
                LogDeallocateFailed(exception, _server.Host);
            }
        }

        CancelPending();
        _connection.Dispose();
        _lifetime.Dispose();
    }

    // Allocate, answering the long-term credential challenge (RFC 8489 section 9.2.5). The ICE loop
    // is not reading yet, so each exchange reads its own response.
    private async Task RequestAllocationAsync(
        AddressFamily family,
        CancellationToken cancellationToken
    )
    {
        Challenge challenge = default;
        while (true)
        {
            byte[] response = await ExchangeDirectAsync(
                    transaction => BuildAllocate(transaction, family),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (!StunMessageReader.TryParse(response, out StunMessageReader message))
            {
                throw new TurnException(0, "The TURN server sent a malformed response.");
            }

            if (message.Class == StunMessageClass.SuccessResponse)
            {
                if (
                    !VerifyIntegrity(message)
                    || !message.TryGetXorAddress(
                        StunAttributeType.XorRelayedAddress,
                        out IPEndPoint relayed
                    )
                )
                {
                    throw new TurnException(
                        0,
                        "The TURN server's allocation response did not verify."
                    );
                }

                // RFC 8656 section 7.3: the relayed address is of the family that was asked for.
                if (relayed.AddressFamily != family)
                {
                    throw new TurnException(
                        0,
                        $"The TURN server relayed {relayed.AddressFamily} where {family} was asked for."
                    );
                }

                LocalEndPoint = relayed;
                MappedAddress = message.TryGetXorMappedAddress(out IPEndPoint mapped)
                    ? mapped
                    : relayed;
                Renew(Lifetime(message));
                LogAllocated(_server.Host, relayed, MappedAddress);
                return;
            }

            if (!HandleChallenge(message, ref challenge, out int code))
            {
                throw new TurnException(code, $"The TURN server refused the allocation ({code}).")
                {
                    AlternateServer =
                        code == 300
                        && message.TryGetAddress(
                            StunAttributeType.AlternateServer,
                            out IPEndPoint alternate
                        )
                        && alternate.AddressFamily == family
                            ? alternate
                            : null,
                };
            }
        }
    }

    // RFC 8489 section 9.2.5: a 401 to a request without credentials carries the realm and nonce to
    // authenticate with, and is answered once; a 401 to one with credentials means they were refused.
    // A 438 carries a fresh nonce and is answered once per request.
    private bool HandleChallenge(StunMessageReader message, ref Challenge challenge, out int code)
    {
        _ = message.TryGetErrorCode(out code);
        if (!message.TryFindAttribute(StunAttributeType.Nonce, out ReadOnlySpan<byte> nonce))
        {
            return false;
        }

        bool answer = code switch
        {
            401 => !challenge.Unauthorized && _key.Length == 0,
            438 => !challenge.StaleNonce,
            _ => false,
        };
        if (!answer)
        {
            return false;
        }

        challenge =
            code == 401
                ? challenge with
                {
                    Unauthorized = true,
                }
                : challenge with
                {
                    StaleNonce = true,
                };
        lock (_gate)
        {
            _nonce = nonce.ToArray();
            if (message.TryFindAttribute(StunAttributeType.Realm, out ReadOnlySpan<byte> realm))
            {
                _realm = realm.ToArray();
                _key = LongTermKey(_server.Username, realm, _server.Credential);
            }
        }

        return _key.Length > 0;
    }

    // Sends a request and reads the connection until its response arrives, retransmitting on UDP.
    private async Task<byte[]> ExchangeDirectAsync(
        Func<UInt128, byte[]> build,
        CancellationToken cancellationToken
    )
    {
        UInt128 transaction = NewTransaction();
        byte[] request = build(transaction);
        byte[] buffer = new byte[2048];
        foreach (TimeSpan wait in Waits())
        {
            await _connection.SendAsync(request, cancellationToken).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(wait, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token,
                cancellationToken
            );
            try
            {
                while (true)
                {
                    int length = await _connection
                        .ReceiveAsync(buffer, linked.Token)
                        .ConfigureAwait(false);
                    if (IsResponseTo(buffer.AsSpan(0, length), transaction))
                    {
                        return buffer[..length];
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // No answer within this wait: the next transmission waits twice as long.
                LogRetransmitting(_server.Host, wait);
            }
        }

        throw new TurnException(0, $"The TURN server {_server.Host} did not answer.");
    }

    // Sends an authenticated request whose response the ICE receive loop delivers, answering the
    // credential challenge as RequestAllocationAsync does.
    private async Task<Outcome> TransactAsync(
        StunMethod method,
        WriteAttributes attributes,
        CancellationToken cancellationToken
    )
    {
        Challenge challenge = default;
        while (true)
        {
            UInt128 transaction = NewTransaction();
            byte[] buffer = new byte[1024];
            byte[] request = buffer[..WriteRequest(buffer, method, transaction, attributes)];
            var completion = new TaskCompletionSource<byte[]>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            lock (_gate)
            {
                _pending[transaction] = completion;
            }

            byte[]? response = null;
            try
            {
                foreach (TimeSpan wait in Waits())
                {
                    await _connection.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        response = await completion
                            .Task.WaitAsync(wait, _time, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                    catch (TimeoutException)
                    {
                        // Deliberately not rethrown: the next transmission waits twice as long.
                        LogRetransmitting(_server.Host, wait);
                    }
                }
            }
            finally
            {
                lock (_gate)
                {
                    _ = _pending.Remove(transaction);
                }
            }

            if (response is null)
            {
                LogRequestTimedOut(method, _server.Host);
                return Outcome.TimedOut;
            }

            if (!StunMessageReader.TryParse(response, out StunMessageReader message))
            {
                return new Outcome(false, 0, null);
            }

            if (message.Class == StunMessageClass.SuccessResponse)
            {
                if (!VerifyIntegrity(message))
                {
                    LogUnverifiedResponse(method, _server.Host);
                    return new Outcome(false, 0, null);
                }

                return new Outcome(true, 0, Lifetime(message));
            }

            if (!HandleChallenge(message, ref challenge, out int code))
            {
                LogRequestRefused(method, code, _server.Host);
                return new Outcome(false, code, null);
            }
        }
    }

    // CreatePermission for one peer address (RFC 8656 section 10), then the datagrams that waited.
    private async Task RequestPermissionAsync(IPAddress address)
    {
        Outcome outcome = await TransactSafelyAsync(
                StunMethod.CreatePermission,
                (ref w) => w.AddXorAddress(StunAttributeType.XorPeerAddress, new(address, 0))
            )
            .ConfigureAwait(false);
        List<(IPEndPoint Destination, byte[] Data)> waiting;
        lock (_gate)
        {
            if (!_permissions.TryGetValue(address, out Permission? permission))
            {
                return;
            }

            waiting = [.. permission.Queue];
            permission.Queue.Clear();
            if (outcome.Succeeded)
            {
                permission.State = PermissionState.Installed;
                permission.RefreshAt = Now + PermissionRefresh;
            }
            else
            {
                permission.State = PermissionState.Failed;
                permission.RetryAt = Now + FailureRetry;
                waiting.Clear();
            }
        }

        if (!outcome.Succeeded)
        {
            if (outcome.Code == 437)
            {
                Fail($"The TURN server {_server.Host} no longer has the allocation (437).");
            }
            else
            {
                LogPermissionFailed(address, outcome.Code, _server.Host);
            }

            return;
        }

        Reschedule();
        foreach ((IPEndPoint destination, byte[] data) in waiting)
        {
            try
            {
                await SendAsync(data, destination, default, _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
                when (exception
                        is OperationCanceledException
                            or ObjectDisposedException
                            or IOException
                            or SocketException
                )
            {
                // Deliberately not logged: a datagram that waited for its permission is lost as any
                // datagram is; the sender retransmits what matters.
                return;
            }
        }
    }

    // Binds the peer's channel, which also refreshes its permission. A failure is retried after a
    // pause on the next send; Send indications carry the traffic meanwhile.
    private async Task BindAsync(Peer peer)
    {
        Outcome outcome = await TransactSafelyAsync(
                StunMethod.ChannelBind,
                (ref w) =>
                {
                    w.AddUInt32(StunAttributeType.ChannelNumber, (uint)peer.Channel << 16);
                    w.AddXorAddress(StunAttributeType.XorPeerAddress, peer.Endpoint);
                }
            )
            .ConfigureAwait(false);
        lock (_gate)
        {
            peer.Binding = false;
            if (outcome.Succeeded)
            {
                peer.Bound = true;
                peer.BoundAt = Now;
            }
            else
            {
                peer.RetryAt = Now + FailureRetry;
            }
        }

        switch (outcome.Code)
        {
            case 437:
                Fail($"The TURN server {_server.Host} no longer has the allocation (437).");
                break;
            case 400 when peer.Bound:
                // RFC 8656 section 12.3: a refused refresh of a bound channel means client and server
                // disagree on the channels; the allocation is deleted and started afresh.
                Fail(
                    $"The TURN server {_server.Host} and this client disagree on channel {peer.Channel}."
                );
                break;
            default:
                if (outcome.Succeeded)
                {
                    Reschedule();
                }

                break;
        }
    }

    // Runs a transaction from a background task, where cancellation or a closed connection is how
    // the allocation ends.
    private async Task<Outcome> TransactSafelyAsync(StunMethod method, WriteAttributes attributes)
    {
        try
        {
            return await TransactAsync(method, attributes, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // Deliberately not logged: the allocation is closing.
            return Outcome.TimedOut;
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            LogTransactionFailed(exception, method, _server.Host);
            return Outcome.TimedOut;
        }
    }

    private Peer PeerFor(IPEndPoint destination)
    {
        if (!_peers.TryGetValue(destination, out Peer? peer))
        {
            // Channel numbers are never reused, which keeps clear of the five minutes a released one
            // must rest (RFC 8656 section 12). With every number taken, a peer is reached by Send
            // indications alone.
            peer =
                _nextChannel > LastChannel
                    ? new Peer(destination, 0)
                    : new Peer(destination, _nextChannel++);
            if (peer.Channel != 0)
            {
                _channels[peer.Channel] = destination;
            }

            _peers[destination] = peer;
        }

        return peer;
    }

    private async ValueTask RelayAsync(
        Peer peer,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken
    )
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(data.Length + 64);
        try
        {
            int length = peer.Bound
                ? WriteChannelData(buffer, peer.Channel, data.Span)
                : WriteSendIndication(buffer, peer.Endpoint, data.Span);
            await _connection
                .SendAsync(buffer.AsMemory(0, length), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // The timer's callback: one maintenance pass at a time.
    private void Maintain()
    {
        if (Interlocked.Exchange(ref _maintaining, 1) != 0)
        {
            return;
        }

        _ = MaintainAsync();
    }

    // Refreshes the allocation a minute before it expires (RFC 8656 section 8), and the permissions
    // and channels a minute before their permissions would lapse.
    private async Task MaintainAsync()
    {
        try
        {
            if (Now >= _refreshAt)
            {
                await RefreshAsync().ConfigureAwait(false);
            }

            List<IPAddress> permissions;
            List<Peer> channels;
            lock (_gate)
            {
                if (_gone is not null)
                {
                    return;
                }

                TimeSpan now = Now;
                channels =
                [
                    .. _peers.Values.Where(p =>
                        p.Bound && !p.Binding && now - p.BoundAt >= PermissionRefresh
                    ),
                ];
                foreach (Peer peer in channels)
                {
                    peer.Binding = true;
                }

                permissions =
                [
                    .. _permissions
                        .Where(p =>
                            p.Value.State == PermissionState.Installed && now >= p.Value.RefreshAt
                        )
                        .Select(p => p.Key),
                ];
            }

            if (permissions.Count > 0)
            {
                await RefreshPermissionsAsync(permissions).ConfigureAwait(false);
            }

            foreach (Peer peer in channels)
            {
                await BindAsync(peer).ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref _maintaining, 0);
            Reschedule();
        }
    }

    private async Task RefreshAsync()
    {
        Outcome outcome = await TransactSafelyAsync(
                StunMethod.Refresh,
                static (ref w) =>
                {
                    w.AddUInt32(StunAttributeType.Lifetime, RequestedLifetime);
                    w.AddAttribute(StunAttributeType.Software, s_software);
                }
            )
            .ConfigureAwait(false);
        if (outcome.Succeeded)
        {
            Renew(outcome.Lifetime ?? TimeSpan.FromSeconds(RequestedLifetime));
            return;
        }

        if (outcome.Code == 437 || Now >= _expires)
        {
            // RFC 8656 section 8.3: a 437 to a refresh means the allocation no longer exists.
            Fail(
                outcome.Code == 437
                    ? $"The TURN server {_server.Host} no longer has the allocation (437)."
                    : $"The TURN allocation on {_server.Host} expired without a refresh."
            );
            return;
        }

        // Unanswered or refused: tried again shortly while the allocation lives.
        lock (_gate)
        {
            _refreshAt = Now + FailureRetry;
        }
    }

    // One CreatePermission refreshes every address that is due (RFC 8656 section 10.1 allows several
    // XOR-PEER-ADDRESS attributes).
    private async Task RefreshPermissionsAsync(List<IPAddress> addresses)
    {
        Outcome outcome = await TransactSafelyAsync(
                StunMethod.CreatePermission,
                (ref w) =>
                {
                    foreach (IPAddress address in addresses)
                    {
                        w.AddXorAddress(StunAttributeType.XorPeerAddress, new(address, 0));
                    }
                }
            )
            .ConfigureAwait(false);
        if (outcome.Code == 437)
        {
            Fail($"The TURN server {_server.Host} no longer has the allocation (437).");
            return;
        }

        lock (_gate)
        {
            foreach (IPAddress address in addresses)
            {
                if (_permissions.TryGetValue(address, out Permission? permission))
                {
                    if (outcome.Succeeded)
                    {
                        permission.RefreshAt = Now + PermissionRefresh;
                    }
                    else
                    {
                        // Lapses at the server within a minute; the next send asks again.
                        permission.State = PermissionState.Failed;
                        permission.RetryAt = Now + FailureRetry;
                    }
                }
            }
        }

        if (!outcome.Succeeded)
        {
            LogPermissionFailed(addresses[0], outcome.Code, _server.Host);
        }
    }

    // When the server granted a lifetime, the refresh is due a minute before it ends; short lifetimes
    // refresh at half, long ones are capped at an hour, as libwebrtc does.
    private void Renew(TimeSpan lifetime)
    {
        TimeSpan delay =
            lifetime < TimeSpan.FromMinutes(2) ? lifetime / 2
            : lifetime > TimeSpan.FromHours(1) ? TimeSpan.FromMinutes(59)
            : lifetime - TimeSpan.FromMinutes(1);
        lock (_gate)
        {
            _expires = Now + lifetime;
            _refreshAt = Now + delay;
        }
    }

    // The time until the next refresh, permission or channel falls due.
    private TimeSpan NextDue()
    {
        lock (_gate)
        {
            TimeSpan next = _refreshAt;
            foreach (Permission permission in _permissions.Values)
            {
                if (permission.State == PermissionState.Installed && permission.RefreshAt < next)
                {
                    next = permission.RefreshAt;
                }
            }

            foreach (Peer peer in _peers.Values)
            {
                if (peer.Bound && peer.BoundAt + PermissionRefresh < next)
                {
                    next = peer.BoundAt + PermissionRefresh;
                }
            }

            TimeSpan due = next - Now;
            return due > TimeSpan.Zero ? due : TimeSpan.Zero;
        }
    }

    private void Reschedule()
    {
        if (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _gone) is null)
        {
            try
            {
                _ = _maintenance?.Change(NextDue(), Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Deliberately not logged: Dispose stopped the timer between the check and the change.
            }
        }
    }

    // The allocation is gone at the server: the receive loop learns it as a closed connection, and
    // nothing more is sent.
    private void Fail(string reason)
    {
        if (Interlocked.CompareExchange(ref _gone, reason, null) is not null)
        {
            return;
        }

        LogAllocationGone(_server.Host, reason);
        _maintenance?.Dispose();
        CancelPending();
        _connection.Dispose();
    }

    private void ThrowIfGone()
    {
        if (Volatile.Read(ref _gone) is { } reason)
        {
            throw new IOException(reason);
        }
    }

    private void CancelPending()
    {
        lock (_gate)
        {
            foreach (TaskCompletionSource<byte[]> pending in _pending.Values)
            {
                _ = pending.TrySetCanceled();
            }

            _pending.Clear();
        }
    }

    // Peer data comes out at the start of the buffer with the peer as its source; responses complete
    // their transactions and yield nothing.
    private IPEndPoint? Unwrap(Span<byte> message, out int dataLength)
    {
        dataLength = 0;
        if (message.Length >= 4 && (message[0] & 0xC0) == 0x40)
        {
            ushort channel = BinaryPrimitives.ReadUInt16BigEndian(message);
            int length = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
            IPEndPoint? peer;
            lock (_gate)
            {
                _ = _channels.TryGetValue(channel, out peer);
            }

            if (peer is null || 4 + length > message.Length)
            {
                return null;
            }

            message.Slice(4, length).CopyTo(message);
            dataLength = length;
            return peer;
        }

        if (!StunMessageReader.TryParse(message, out StunMessageReader stun))
        {
            return null;
        }

        if (stun.Class == StunMessageClass.Indication && stun.Method == StunMethod.Data)
        {
            if (
                !stun.TryGetXorAddress(StunAttributeType.XorPeerAddress, out IPEndPoint peer)
                || !stun.TryFindAttribute(StunAttributeType.Data, out ReadOnlySpan<byte> data)
            )
            {
                return null;
            }

            // RFC 8656 section 11.4: data from an address without an active permission is dropped.
            lock (_gate)
            {
                if (
                    !_permissions.TryGetValue(peer.Address, out Permission? permission)
                    || permission.State != PermissionState.Installed
                )
                {
                    return null;
                }
            }

            dataLength = data.Length;
            data.CopyTo(message);
            return peer;
        }

        if (stun.Class is StunMessageClass.SuccessResponse or StunMessageClass.ErrorResponse)
        {
            TaskCompletionSource<byte[]>? pending;
            lock (_gate)
            {
                _ = _pending.TryGetValue(ReadTransaction(stun.TransactionId), out pending);
            }

            _ = pending?.TrySetResult(stun.Raw.ToArray());
        }

        return null;
    }

    // How long to wait after each transmission: RTO doubling over UDP, the last wait Rm times the
    // initial RTO (RFC 8489 section 6.2.1); one wait of Ti over TCP and TLS.
    private IEnumerable<TimeSpan> Waits()
    {
        if (_connection.IsReliable)
        {
            yield return ReliableTimeout;
            yield break;
        }

        TimeSpan rto = InitialRto;
        for (int transmission = 1; transmission < UdpTransmissions; transmission++)
        {
            yield return rto;
            rto *= 2;
        }

        yield return InitialRto * LastWaitFactor;
    }

    private bool VerifyIntegrity(StunMessageReader message) =>
        _key.Length > 0 && message.VerifyMessageIntegrity(_key);

    private byte[] BuildAllocate(UInt128 transaction, AddressFamily family)
    {
        byte[] buffer = new byte[1024];
        return buffer[
            ..WriteRequest(
                buffer,
                StunMethod.Allocate,
                transaction,
                (ref w) =>
                {
                    // UDP relaying (protocol 17), of the family the client reached the server with.
                    w.AddUInt32(StunAttributeType.RequestedTransport, 17u << 24);
                    w.AddUInt32(
                        StunAttributeType.RequestedAddressFamily,
                        family == AddressFamily.InterNetworkV6 ? 0x02u << 24 : 0x01u << 24
                    );
                    w.AddUInt32(StunAttributeType.Lifetime, RequestedLifetime);
                    w.AddAttribute(StunAttributeType.Software, s_software);
                }
            )
        ];
    }

    // Writes a request with the long-term credential attributes once the server has challenged.
    private int WriteRequest(
        Span<byte> buffer,
        StunMethod method,
        UInt128 transaction,
        WriteAttributes attributes
    )
    {
        Span<byte> id = stackalloc byte[StunHeader.TransactionIdLength];
        WriteTransaction(transaction, id);
        StunMessageWriter writer = new(buffer, StunMessageClass.Request, method, id);
        attributes(ref writer);
        lock (_gate)
        {
            if (_key.Length > 0)
            {
                writer.AddAttribute(
                    StunAttributeType.Username,
                    Encoding.UTF8.GetBytes(_server.Username)
                );
                writer.AddAttribute(StunAttributeType.Realm, _realm);
                writer.AddAttribute(StunAttributeType.Nonce, _nonce);
                writer.AddMessageIntegrity(_key);
            }
        }

        writer.AddFingerprint();
        return writer.Length;
    }

    private static int WriteSendIndication(
        Span<byte> buffer,
        IPEndPoint peer,
        ReadOnlySpan<byte> data
    )
    {
        Span<byte> id = stackalloc byte[StunHeader.TransactionIdLength];
        RandomNumberGenerator.Fill(id);
        StunMessageWriter writer = new(buffer, StunMessageClass.Indication, StunMethod.Send, id);
        writer.AddXorAddress(StunAttributeType.XorPeerAddress, peer);
        writer.AddAttribute(StunAttributeType.Data, data);
        return writer.Length;
    }

    private static int WriteChannelData(Span<byte> buffer, ushort channel, ReadOnlySpan<byte> data)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buffer, channel);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], (ushort)data.Length);
        data.CopyTo(buffer[4..]);
        return 4 + data.Length;
    }

    private static bool IsResponseTo(ReadOnlySpan<byte> message, UInt128 transaction) =>
        StunMessageReader.TryParse(message, out StunMessageReader stun)
        && stun.Class is StunMessageClass.SuccessResponse or StunMessageClass.ErrorResponse
        && ReadTransaction(stun.TransactionId) == transaction;

    private static TimeSpan Lifetime(StunMessageReader message) =>
        TimeSpan.FromSeconds(
            message.TryGetUInt32(StunAttributeType.Lifetime, out uint seconds)
                ? seconds
                : RequestedLifetime
        );

    // The long-term credential key: MD5(username ":" realm ":" password) (RFC 8489 section 9.2.2).
#pragma warning disable CA5351 // The STUN long-term credential mechanism defines the key as MD5.
    private static byte[] LongTermKey(string username, ReadOnlySpan<byte> realm, string password) =>
        MD5.HashData([
            .. Encoding.UTF8.GetBytes(username),
            (byte)':',
            .. realm,
            (byte)':',
            .. Encoding.UTF8.GetBytes(password),
        ]);
#pragma warning restore CA5351

    private static UInt128 NewTransaction()
    {
        Span<byte> id = stackalloc byte[StunHeader.TransactionIdLength];
        RandomNumberGenerator.Fill(id);
        return ReadTransaction(id);
    }

    private static UInt128 ReadTransaction(ReadOnlySpan<byte> id) =>
        new(
            BinaryPrimitives.ReadUInt32BigEndian(id),
            BinaryPrimitives.ReadUInt64BigEndian(id[4..])
        );

    private static void WriteTransaction(UInt128 transaction, Span<byte> id)
    {
        BinaryPrimitives.WriteUInt32BigEndian(id, (uint)(transaction >> 64));
        BinaryPrimitives.WriteUInt64BigEndian(id[4..], (ulong)transaction);
    }

    [LoggerMessage(
        EventId = 1150,
        Level = LogLevel.Information,
        Message = "TURN allocation on {Server}: relayed {Relayed}, mapped {Mapped}"
    )]
    private partial void LogAllocated(string server, IPEndPoint relayed, IPEndPoint mapped);

    [LoggerMessage(
        EventId = 1151,
        Level = LogLevel.Warning,
        Message = "TURN {Method} to {Server} timed out"
    )]
    private partial void LogRequestTimedOut(StunMethod method, string server);

    [LoggerMessage(
        EventId = 1152,
        Level = LogLevel.Warning,
        Message = "TURN {Method} refused by {Server} with {Code}"
    )]
    private partial void LogRequestRefused(StunMethod method, int code, string server);

    [LoggerMessage(
        EventId = 1153,
        Level = LogLevel.Warning,
        Message = "TURN {Method} to {Server} failed"
    )]
    private partial void LogTransactionFailed(
        Exception exception,
        StunMethod method,
        string server
    );

    [LoggerMessage(
        EventId = 1154,
        Level = LogLevel.Warning,
        Message = "TURN allocation on {Server} is gone: {Reason}"
    )]
    private partial void LogAllocationGone(string server, string reason);

    [LoggerMessage(
        EventId = 1155,
        Level = LogLevel.Debug,
        Message = "TURN deallocation on {Server} was not sent"
    )]
    private partial void LogDeallocateFailed(Exception exception, string server);

    [LoggerMessage(
        EventId = 1156,
        Level = LogLevel.Debug,
        Message = "TURN server {Server} did not answer within {Timeout}; retransmitting."
    )]
    private partial void LogRetransmitting(string server, TimeSpan timeout);

    [LoggerMessage(
        EventId = 1183,
        Level = LogLevel.Warning,
        Message = "TURN permission for {Peer} on {Server} failed ({Code}); traffic to it is dropped until it is asked for again"
    )]
    private partial void LogPermissionFailed(IPAddress peer, int code, string server);

    [LoggerMessage(
        EventId = 1184,
        Level = LogLevel.Information,
        Message = "TURN server {Server} sees this 5-tuple in use (437, attempt {Attempt}); allocating from another port"
    )]
    private partial void LogAllocationMismatch(string server, int attempt);

    [LoggerMessage(
        EventId = 1185,
        Level = LogLevel.Information,
        Message = "TURN server {Server} redirected the allocation to {Alternate}"
    )]
    private partial void LogRedirected(string server, IPEndPoint alternate);

    [LoggerMessage(
        EventId = 1186,
        Level = LogLevel.Warning,
        Message = "TURN {Method} response from {Server} did not verify; it is ignored"
    )]
    private partial void LogUnverifiedResponse(StunMethod method, string server);

    private delegate void WriteAttributes(ref StunMessageWriter writer);

    // What a transaction came to: success with the granted lifetime, or the error code (0 when the
    // server did not answer or the answer did not verify).
    private readonly record struct Outcome(bool Succeeded, int Code, TimeSpan? Lifetime)
    {
        public static Outcome TimedOut => new(false, 0, null);
    }

    // Which challenges a request has answered.
    private readonly record struct Challenge(bool Unauthorized, bool StaleNonce);

    private enum PermissionState
    {
        Requesting,
        Installed,
        Failed,
    }

    private sealed class Permission
    {
        public PermissionState State { get; set; }

        public TimeSpan RefreshAt { get; set; }

        public TimeSpan RetryAt { get; set; }

        public Queue<(IPEndPoint Destination, byte[] Data)> Queue { get; } = new();
    }

    private sealed class Peer(IPEndPoint endpoint, ushort channel)
    {
        public IPEndPoint Endpoint { get; } = endpoint;

        public ushort Channel { get; } = channel;

        public bool Bound { get; set; }

        public bool Binding { get; set; }

        public TimeSpan BoundAt { get; set; }

        public TimeSpan RetryAt { get; set; }
    }
}

/// <summary>A TURN server refused or did not answer a request.</summary>
public sealed class TurnException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TurnException() { }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public TurnException(string message)
        : base(message) { }

    /// <summary>Creates the exception with a message and its cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public TurnException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates the exception for a STUN error code.</summary>
    /// <param name="code">The STUN error code, or 0 when the server sent none.</param>
    /// <param name="message">What went wrong.</param>
    public TurnException(int code, string message)
        : base(message) => Code = code;

    /// <summary>The STUN error code (RFC 8489 section 14.8), or 0 when the server sent none.</summary>
    public int Code { get; }

    // The server a 300 (Try Alternate) pointed to, of the request's address family.
    internal IPEndPoint? AlternateServer { get; init; }
}
