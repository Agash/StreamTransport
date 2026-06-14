using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Agash.StreamTransport.WebRtc.Stun;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.WebRtc.Ice;

/// <summary>
/// A full-ICE agent (RFC 8445) over UDP with trickle (RFC 8838): it gathers host candidates (one socket
/// per local address, so the source address is pinned - the durable fix for IPv6 source rotation), runs
/// STUN connectivity checks against trickled remote candidates, performs regular nomination, and maintains
/// consent freshness (RFC 7675) on the selected pair. Non-STUN datagrams on the agent's sockets (DTLS,
/// SRTP) are surfaced via <see cref="DataReceived"/>; outbound media goes through <see cref="SendAsync"/>.
/// </summary>
public sealed partial class IceAgent : IAsyncDisposable
{
    private const int MaxCheckTransmits = 7;
    private readonly IceTimings _timings;

    private readonly IceRole _role;
    private readonly ulong _tieBreaker;
    private readonly bool _includeLoopback;
    private readonly ILogger _logger;
    private byte[] _localPwdBytes;
    private readonly IIceSocketFactory _socketFactory;

    private readonly List<LocalEndpoint> _localEndpoints = [];
    private readonly List<IceCandidate> _remoteCandidates = [];
    private readonly List<CandidatePair> _pairs = [];
    private readonly List<IPEndPoint> _stunServers = [];
    private readonly ConcurrentDictionary<string, CandidatePair> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GatherTransaction> _gatherInFlight = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private IceCredentials _remote;
    private CandidatePair? _selected;
    private CancellationTokenSource? _cts;
    private Task? _checkLoop;
    private int _candidateIndex;
    private int _state = (int)IceConnectionState.New;

    /// <summary>
    /// Creates an agent. <paramref name="role"/> follows the offer/answer (offerer = controlling).
    /// <paramref name="socketFactory"/> defaults to real UDP; tests inject an in-memory one.
    /// </summary>
    public IceAgent(
        IceCredentials localCredentials,
        IceRole role,
        bool includeLoopback = false,
        ILogger<IceAgent>? logger = null,
        IIceSocketFactory? socketFactory = null,
        IceTimings? timings = null)
    {
        LocalCredentials = localCredentials;
        _role = role;
        _includeLoopback = includeLoopback;
        _logger = logger ?? NullLogger<IceAgent>.Instance;
        _socketFactory = socketFactory ?? new UdpIceSocketFactory();
        _timings = timings ?? IceTimings.Default;
        _localPwdBytes = Encoding.UTF8.GetBytes(localCredentials.Password);
        Span<byte> tb = stackalloc byte[8];
        RandomNumberGenerator.Fill(tb);
        _tieBreaker = BinaryPrimitives.ReadUInt64BigEndian(tb);
    }

    /// <summary>Raised once per gathered local candidate (trickle these to the peer).</summary>
    public event Action<IceCandidate>? LocalCandidateGathered;

    /// <summary>Raised when the connection state changes.</summary>
    public event Action<IceConnectionState>? StateChanged;

    /// <summary>
    /// Raised for every non-STUN datagram received (DTLS / SRTP), with the source endpoint and the packet's
    /// 2-bit ECN mark (0 when the platform does not surface it). The buffer is the agent's <b>reused</b> receive
    /// buffer, borrowed only for the synchronous duration of the handler: the handler may read and mutate it in
    /// place (e.g. SRTP decrypts into it) but <b>must copy anything it retains</b> beyond the call, because the
    /// next datagram overwrites it. This is zero-copy by design.
    /// </summary>
    public event Action<Memory<byte>, IPEndPoint, byte>? DataReceived;

    /// <summary>The current connection state.</summary>
    public IceConnectionState State => (IceConnectionState)Volatile.Read(ref _state);

    /// <summary>The local endpoint of the currently selected candidate pair, or null if none is selected.</summary>
    public IPEndPoint? SelectedLocalEndpoint => _selected?.Local.Candidate.Endpoint;

    /// <summary>This agent's local credentials (rotated on an ICE restart).</summary>
    public IceCredentials LocalCredentials { get; private set; }

    /// <summary>Sets the remote agent's credentials (from the peer's SDP). Required before checks can pass.</summary>
    public void SetRemoteCredentials(IceCredentials remote) => _remote = remote;

    /// <summary>
    /// Adds a STUN server to query for server-reflexive candidates (the public mapping behind a NAT).
    /// IPv6 global addresses already appear as host candidates, so this is chiefly the IPv4 NAT-punch path.
    /// Call before <see cref="Start"/>.
    /// </summary>
    public void AddStunServer(IPEndPoint server) => _stunServers.Add(server);

    /// <summary>
    /// Binds a UDP socket per local address and raises <see cref="LocalCandidateGathered"/> for each host
    /// candidate, then starts the connectivity-check loop. Idempotent gathering is not supported - call once.
    /// </summary>
    public void Start()
    {
        GatherHostCandidates();

        // Pair against any remote candidates that arrived (trickled) before we gathered.
        lock (_gate)
        {
            foreach (IceCandidate remote in _remoteCandidates)
            {
                FormPairsFor(remote);
            }
        }

        _cts = new CancellationTokenSource();
        foreach (LocalEndpoint ep in _localEndpoints)
        {
            ep.ReceiveTask = ReceiveLoopAsync(ep, _cts.Token);
        }

        _checkLoop = CheckLoopAsync(_cts.Token);
        SetState(IceConnectionState.Checking);

        if (_stunServers.Count > 0)
        {
            SendServerReflexiveProbes();
        }
    }

    private void SendServerReflexiveProbes()
    {
        Span<byte> txId = stackalloc byte[StunHeader.TransactionIdLength];
        foreach (LocalEndpoint local in _localEndpoints)
        {
            foreach (IPEndPoint server in _stunServers)
            {
                if (server.AddressFamily != local.Candidate.Endpoint.AddressFamily)
                {
                    continue;
                }

                RandomNumberGenerator.Fill(txId);
                byte[] buffer = new byte[64];
                var writer = new StunMessageWriter(buffer, StunMessageClass.Request, StunMethod.Binding, txId);
                writer.AddFingerprint();
                _gatherInFlight[Convert.ToHexString(txId)] = new GatherTransaction(local, server);
                _ = local.Socket.SendAsync(buffer.AsMemory(0, writer.Length), server);
            }
        }
    }

    /// <summary>Adds a remote candidate learned via signaling (trickle ICE).</summary>
    public void AddRemoteCandidate(IceCandidate candidate)
    {
        lock (_gate)
        {
            if (_remoteCandidates.Contains(candidate))
            {
                return;
            }

            _remoteCandidates.Add(candidate);
            FormPairsFor(candidate);
        }

        LogRemoteCandidate(_logger, candidate.Kind, candidate.Endpoint);
    }

    /// <summary>
    /// Sends a media/DTLS datagram over the selected pair. During the brief no-pair window of a mobility
    /// recovery (consent loss / network change) the datagram is dropped rather than thrown - those packets
    /// would be lost on the dead path anyway, and the media pump must not fault while ICE re-nominates.
    /// </summary>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        CandidatePair? pair = _selected;
        if (pair is null)
        {
            return;
        }

        await pair.Local.Socket.SendAsync(data, pair.Remote.Endpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-probe all candidate pairs and re-nominate (mobility recovery): on consent loss or a network change,
    /// drop the selected pair and re-run connectivity checks so the agent fails over to whatever path now
    /// works - including a peer-reflexive pair from the peer's new source address. The DTLS-SRTP session is
    /// untouched (it is bound to the peer's certificate, not the path), so keys + the RTP rollover counter are
    /// preserved across the switch - QUIC-style connection migration applied to SRTP.
    /// </summary>
    public void TriggerRecovery()
    {
        lock (_gate)
        {
            _selected = null;
            foreach (CandidatePair p in _pairs)
            {
                p.State = PairState.Waiting;
                p.Nominated = false;
                p.NominationCheck = false;
                p.TriggeredCheck = false;
                p.Transmits = 0;
            }
        }

        SetState(IceConnectionState.Checking);
        LogRecovery(_logger);
    }

    /// <summary>
    /// Full ICE restart (RFC 8445 §9 / RFC 8829): adopt fresh local credentials, re-gather host candidates
    /// (new ports, re-trickled to the peer), and re-pair/re-check under the new credentials. The DTLS-SRTP
    /// session is untouched - keys + the RTP rollover counter survive - so media continues across the restart.
    /// Used when a credential rollover is needed (e.g. a full network re-attach), beyond what
    /// <see cref="TriggerRecovery"/> (re-probe existing pairs) covers.
    /// </summary>
    public void Restart(IceCredentials newLocalCredentials)
    {
        lock (_gate)
        {
            LocalCredentials = newLocalCredentials;
            _localPwdBytes = Encoding.UTF8.GetBytes(newLocalCredentials.Password);
            _selected = null;
            _inFlight.Clear();
            _pairs.Clear();

            foreach (LocalEndpoint ep in _localEndpoints)
            {
                ep.Socket.Dispose();
            }
            _localEndpoints.Clear();
        }
        // Re-gather fresh host candidates under the new credentials and re-trickle them.
        GatherHostCandidates();

        CancellationToken ct = _cts?.Token ?? CancellationToken.None;
        lock (_gate)
        {
            foreach (LocalEndpoint ep in _localEndpoints)
            {
                ep.ReceiveTask ??= ReceiveLoopAsync(ep, ct);
            }

            // Re-pair the freshly gathered local candidates against the remote candidates we already know.
            foreach (IceCandidate remote in _remoteCandidates)
            {
                FormPairsFor(remote);
            }
        }

        SetState(IceConnectionState.Checking);
        LogRestart(_logger);
    }

    private void GatherHostCandidates()
    {
        foreach (IPAddress address in _socketFactory.GetLocalAddresses(_includeLoopback))
        {
            if (!_socketFactory.TryBind(address, out IIceSocket socket))
            {
                continue; // family unavailable / address not bindable - skip.
            }

            IPEndPoint bound = socket.LocalEndPoint;
            int index = _candidateIndex++;
            uint priority = IceCandidate.ComputePriority(IceCandidateKind.Host, address.AddressFamily, IceCandidate.RtpComponent, index);
            string foundation = Foundation(IceCandidateKind.Host, address);
            var candidate = new IceCandidate(foundation, IceCandidate.RtpComponent, priority, bound, IceCandidateKind.Host);

            var ep = new LocalEndpoint(candidate, socket);
            _localEndpoints.Add(ep);
            LogLocalCandidate(_logger, candidate.Kind, candidate.Endpoint);
            LocalCandidateGathered?.Invoke(candidate);
        }
    }

    private static string Foundation(IceCandidateKind kind, IPAddress baseAddress)
        => $"{(int)kind}-{baseAddress}";

    private void FormPairsFor(IceCandidate remote)
    {
        foreach (LocalEndpoint local in _localEndpoints)
        {
            if (local.Candidate.Endpoint.AddressFamily != remote.Endpoint.AddressFamily
                || local.Candidate.ComponentId != remote.ComponentId)
            {
                continue; // pairs are within an address family and component.
            }

            uint controlling = _role == IceRole.Controlling ? local.Candidate.Priority : remote.Priority;
            uint controlled = _role == IceRole.Controlling ? remote.Priority : local.Candidate.Priority;
            ulong pairPriority = IceCandidate.ComputePairPriority(controlling, controlled);
            _pairs.Add(new CandidatePair(local, remote, pairPriority));
        }

        _pairs.Sort(static (a, b) => b.Priority.CompareTo(a.Priority));
    }

    private async Task ReceiveLoopAsync(LocalEndpoint local, CancellationToken ct)
    {
        byte[] buffer = new byte[2048];
        while (!ct.IsCancellationRequested)
        {
            IceReceiveResult result;
            try
            {
                result = await local.Socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue; // transient ICMP port-unreachable etc.
            }

            IPEndPoint source = result.RemoteEndPoint;
            ReadOnlySpan<byte> datagram = buffer.AsSpan(0, result.Length);

            if (StunMessageReader.TryParse(datagram, out StunMessageReader stun))
            {
                HandleStun(stun, local, source);
            }
            else
            {
                // DTLS / SRTP - hand the reused receive buffer directly (zero copy). The handler runs
                // synchronously before the next receive, so it may decrypt in place and read it freely; it
                // must copy anything it keeps beyond the call (see DataReceived's contract).
                DataReceived?.Invoke(buffer.AsMemory(0, result.Length), source, result.Ecn);
            }
        }
    }

    private void HandleStun(StunMessageReader stun, LocalEndpoint local, IPEndPoint source)
    {
        switch (stun.Class)
        {
            case StunMessageClass.Request when stun.Method == StunMethod.Binding:
                HandleBindingRequest(stun, local, source);
                break;
            case StunMessageClass.SuccessResponse when stun.Method == StunMethod.Binding:
                HandleBindingSuccess(stun, source);
                break;
            default:
                break; // error responses / indications: ignored for now.
        }
    }

    private void HandleBindingRequest(StunMessageReader stun, LocalEndpoint local, IPEndPoint source)
    {
        // Authenticate: USERNAME must be localUfrag:remoteUfrag, MESSAGE-INTEGRITY keyed by our password.
        if (!stun.VerifyMessageIntegrity(_localPwdBytes))
        {
            return;
        }

        bool useCandidate = stun.TryFindAttribute(StunAttributeType.UseCandidate, out _);

        // Respond: XOR-MAPPED-ADDRESS of the source, MI keyed by our password, FINGERPRINT.
        byte[] response = new byte[128];
        var writer = new StunMessageWriter(response, StunMessageClass.SuccessResponse, StunMethod.Binding, stun.TransactionId);
        writer.AddXorMappedAddress(source);
        writer.AddMessageIntegrity(_localPwdBytes);
        writer.AddFingerprint();
        _ = local.Socket.SendAsync(response.AsMemory(0, writer.Length), source);

        CandidatePair pair = EnsurePairForPeerReflexive(local, source);

        // The remote reached us → this pair is usable from their side. Trigger a check back (triggered
        // check), and on the controlled side honour USE-CANDIDATE nomination.
        pair.RemoteRequestSeen = true;

        // An authenticated inbound binding request proves this path is alive in the receive direction right
        // now, independent of whether our own outbound consent check happened to be lost. Consent is
        // bidirectional in practice (both agents probe the selected pair), so refresh liveness on it too,
        // mirroring libwebrtc's last-received tracking. Without this, a flaky link refreshes consent only on
        // our check *responses* (round-trip survival) and discards the peer's checks (one-way survival),
        // tripping false consent loss under loss that an alive path should ride out.
        pair.LastResponseUtc = DateTime.UtcNow;
        if (useCandidate && _role == IceRole.Controlled && pair.State == PairState.Succeeded)
        {
            Nominate(pair);
        }

        if (pair.State is PairState.Frozen or PairState.Failed)
        {
            pair.State = PairState.Waiting;
        }

        pair.TriggeredCheck = true;
    }

    private void HandleBindingSuccess(StunMessageReader stun, IPEndPoint source)
    {
        string txKey = Convert.ToHexString(stun.TransactionId);

        if (_gatherInFlight.TryRemove(txKey, out GatherTransaction gather))
        {
            HandleServerReflexiveResponse(stun, gather);
            return;
        }

        if (!_inFlight.TryRemove(txKey, out CandidatePair? pair))
        {
            return;
        }

        // Response MI is keyed by the responder's (remote) password.
        if (_remote.Password is { Length: > 0 } && !stun.VerifyMessageIntegrity(Encoding.UTF8.GetBytes(_remote.Password)))
        {
            return;
        }

        pair.State = PairState.Succeeded;
        pair.LastResponseUtc = DateTime.UtcNow;
        LogPairSucceeded(_logger, pair.Local.Candidate.Endpoint, pair.Remote.Endpoint);

        if (pair.NominationCheck)
        {
            Nominate(pair);
        }
        else if (_role == IceRole.Controlling && _selected is null)
        {
            // Regular nomination: nominate the highest-priority valid pair.
            pair.NominationCheck = true;
            pair.TriggeredCheck = true;
        }
    }

    private void HandleServerReflexiveResponse(StunMessageReader stun, GatherTransaction gather)
    {
        if (!stun.TryGetXorMappedAddress(out IPEndPoint mapped))
        {
            return;
        }

        // No NAT in front of this interface (mapped == base) → the host candidate already covers it.
        if (mapped.Equals(gather.Local.Candidate.Endpoint))
        {
            return;
        }

        int index = _candidateIndex++;
        uint priority = IceCandidate.ComputePriority(IceCandidateKind.ServerReflexive, mapped.AddressFamily, IceCandidate.RtpComponent, index);
        var candidate = new IceCandidate(
            Foundation(IceCandidateKind.ServerReflexive, gather.Local.Candidate.Endpoint.Address),
            IceCandidate.RtpComponent, priority, mapped, IceCandidateKind.ServerReflexive,
            relatedAddress: gather.Local.Candidate.Endpoint);

        LogLocalCandidate(_logger, candidate.Kind, candidate.Endpoint);
        LocalCandidateGathered?.Invoke(candidate);
    }

    private void Nominate(CandidatePair pair)
    {
        if (_selected is not null)
        {
            return;
        }

        pair.Nominated = true;
        _selected = pair;
        pair.LastResponseUtc = DateTime.UtcNow;
        LogSelectedPair(_logger, pair.Local.Candidate.Endpoint, pair.Remote.Endpoint);
        SetState(IceConnectionState.Connected);
    }

    private CandidatePair EnsurePairForPeerReflexive(LocalEndpoint local, IPEndPoint source)
    {
        lock (_gate)
        {
            foreach (CandidatePair p in _pairs)
            {
                if (ReferenceEquals(p.Local, local) && p.Remote.Endpoint.Equals(source))
                {
                    return p;
                }
            }

            uint prflxPriority = IceCandidate.ComputePriority(IceCandidateKind.PeerReflexive, source.AddressFamily, IceCandidate.RtpComponent, _candidateIndex++);
            var prflx = new IceCandidate(Foundation(IceCandidateKind.PeerReflexive, source.Address), IceCandidate.RtpComponent, prflxPriority, source, IceCandidateKind.PeerReflexive);
            uint controlling = _role == IceRole.Controlling ? local.Candidate.Priority : prflx.Priority;
            uint controlled = _role == IceRole.Controlling ? prflx.Priority : local.Candidate.Priority;
            var pair = new CandidatePair(local, prflx, IceCandidate.ComputePairPriority(controlling, controlled));
            _pairs.Add(pair);
            _pairs.Sort(static (a, b) => b.Priority.CompareTo(a.Priority));
            return pair;
        }
    }

    private async Task CheckLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_timings.Ta, ct).ConfigureAwait(false);
                if (_remote.Password is not { Length: > 0 })
                {
                    continue; // can't key checks until we have the remote password.
                }

                SendNextCheck();
                MaintainConsent();
                MaintainHotStandby();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Keep non-selected succeeded pairs warm: a low-frequency STUN ping on each (RFC 7675 cadence) so an
    // alternate path stays validated and ready, and a failover can promote it in ~one RTT instead of a fresh
    // gather. libwebrtc's continual-gathering model (stable_writable_connection_ping_interval).
    private void MaintainHotStandby()
    {
        DateTime now = DateTime.UtcNow;
        List<CandidatePair>? toPing = null;
        lock (_gate)
        {
            foreach (CandidatePair p in _pairs)
            {
                if (ReferenceEquals(p, _selected) || p.State != PairState.Succeeded)
                {
                    continue;
                }

                if (now - p.LastSentUtc > _timings.ConsentInterval)
                {
                    p.LastSentUtc = now;
                    (toPing ??= []).Add(p);
                }
            }
        }

        if (toPing is not null)
        {
            foreach (CandidatePair p in toPing)
            {
                SendBindingCheck(p);
            }
        }
    }

    // The highest-priority validated alternate that has answered within the consent window - a pre-warmed pair
    // ready to take over.
    private CandidatePair? BestWarmAlternate(DateTime now, CandidatePair exclude)
    {
        lock (_gate)
        {
            CandidatePair? best = null;
            foreach (CandidatePair p in _pairs)
            {
                if (ReferenceEquals(p, exclude) || p.State != PairState.Succeeded || now - p.LastResponseUtc > _timings.ConsentTimeout)
                {
                    continue;
                }

                if (best is null || p.Priority > best.Priority)
                {
                    best = p;
                }
            }

            return best;
        }
    }

    private void SwitchTo(CandidatePair pair)
    {
        lock (_gate)
        {
            _selected = pair;
            pair.Nominated = true;
        }

        LogSwitched(_logger, pair.Local.Candidate.Endpoint, pair.Remote.Endpoint);
        SetState(IceConnectionState.Connected);
    }

    private void SendNextCheck()
    {
        CandidatePair? toCheck = null;
        DateTime now = DateTime.UtcNow;
        lock (_gate)
        {
            // Triggered checks first, then the highest-priority waiting/retransmit-due pair.
            foreach (CandidatePair p in _pairs)
            {
                if (p.TriggeredCheck)
                {
                    toCheck = p;
                    break;
                }
            }

            toCheck ??= NextOrdinaryCheck(now);
            if (toCheck is null)
            {
                return;
            }

            toCheck.TriggeredCheck = false;
            toCheck.State = PairState.InProgress;
            toCheck.LastSentUtc = now;
            toCheck.Transmits++;
        }

        SendBindingCheck(toCheck);
    }

    private CandidatePair? NextOrdinaryCheck(DateTime now)
    {
        foreach (CandidatePair p in _pairs)
        {
            if (p.State is PairState.Frozen or PairState.Waiting)
            {
                return p;
            }

            if (p.State == PairState.InProgress && now - p.LastSentUtc > _timings.CheckRto)
            {
                if (p.Transmits >= MaxCheckTransmits)
                {
                    p.State = PairState.Failed;
                    continue;
                }

                return p; // retransmit
            }
        }

        return null;
    }

    private void SendBindingCheck(CandidatePair pair)
    {
        Span<byte> txId = stackalloc byte[StunHeader.TransactionIdLength];
        RandomNumberGenerator.Fill(txId);

        byte[] buffer = new byte[160];
        var writer = new StunMessageWriter(buffer, StunMessageClass.Request, StunMethod.Binding, txId);

        string username = IceCredentials.CheckUsername(_remote.UsernameFragment, LocalCredentials.UsernameFragment);
        writer.AddAttribute(StunAttributeType.Username, Encoding.UTF8.GetBytes(username));

        Span<byte> priority = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(priority, IceCandidate.ComputePriority(IceCandidateKind.PeerReflexive, pair.Local.Candidate.Endpoint.AddressFamily, IceCandidate.RtpComponent));
        writer.AddAttribute(StunAttributeType.Priority, priority);

        Span<byte> tie = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(tie, _tieBreaker);
        writer.AddAttribute(_role == IceRole.Controlling ? StunAttributeType.IceControlling : StunAttributeType.IceControlled, tie);

        if (_role == IceRole.Controlling && pair.NominationCheck)
        {
            writer.AddAttribute(StunAttributeType.UseCandidate, default);
        }

        writer.AddMessageIntegrity(Encoding.UTF8.GetBytes(_remote.Password));
        writer.AddFingerprint();

        _inFlight[Convert.ToHexString(txId)] = pair;
        _ = pair.Local.Socket.SendAsync(buffer.AsMemory(0, writer.Length), pair.Remote.Endpoint);
    }

    private void MaintainConsent()
    {
        if (_selected is not { } selected)
        {
            return;
        }

        // Consent is tracked per the SELECTED pair, not globally: hot-standby keep-alives on the alternates
        // also produce binding responses, so a global timer would never see the selected path die.
        DateTime now = DateTime.UtcNow;
        if (now - selected.LastResponseUtc > _timings.ConsentTimeout)
        {
            LogConsentLost(_logger, selected.Remote.Endpoint);

            // Prefer an instant switch to a pre-warmed alternate (hot-standby) - ~one RTT. Only if none is
            // warm do we fall back to a full re-probe (path recovery). Either way SRTP is preserved.
            CandidatePair? warm = BestWarmAlternate(now, selected);
            if (warm is not null)
            {
                SwitchTo(warm);
            }
            else
            {
                TriggerRecovery();
            }

            return;
        }

        if (now - selected.LastSentUtc > _timings.ConsentInterval)
        {
            selected.LastSentUtc = now;
            selected.State = PairState.InProgress;
            SendBindingCheck(selected);
        }
    }

    private void SetState(IceConnectionState state)
    {
        if (Volatile.Read(ref _state) == (int)state)
        {
            return;
        }

        Volatile.Write(ref _state, (int)state);
        StateChanged?.Invoke(state);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // Idempotent: callers commonly StopAsync() then dispose, which routes here twice.
        CancellationTokenSource? cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);

        if (_checkLoop is { } loop)
        {
            try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        foreach (LocalEndpoint ep in _localEndpoints)
        {
            ep.Socket.Dispose();
        }

        cts.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "ICE local candidate {Kind} {Endpoint}")]
    private static partial void LogLocalCandidate(ILogger logger, IceCandidateKind kind, IPEndPoint endpoint);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ICE remote candidate {Kind} {Endpoint}")]
    private static partial void LogRemoteCandidate(ILogger logger, IceCandidateKind kind, IPEndPoint endpoint);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ICE pair succeeded {Local} -> {Remote}")]
    private static partial void LogPairSucceeded(ILogger logger, IPEndPoint local, IPEndPoint remote);

    [LoggerMessage(Level = LogLevel.Information, Message = "ICE selected pair {Local} -> {Remote}")]
    private static partial void LogSelectedPair(ILogger logger, IPEndPoint local, IPEndPoint remote);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ICE consent lost to {Remote}")]
    private static partial void LogConsentLost(ILogger logger, IPEndPoint remote);

    [LoggerMessage(Level = LogLevel.Information, Message = "ICE recovery: re-probing candidate pairs (SRTP session preserved)")]
    private static partial void LogRecovery(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "ICE switched to warm pair {Local} -> {Remote} (SRTP session preserved)")]
    private static partial void LogSwitched(ILogger logger, IPEndPoint local, IPEndPoint remote);

    [LoggerMessage(Level = LogLevel.Information, Message = "ICE restart: re-gathering under fresh credentials (SRTP session preserved)")]
    private static partial void LogRestart(ILogger logger);

    private readonly record struct GatherTransaction(IceAgent.LocalEndpoint Local, IPEndPoint Server);

    private sealed class LocalEndpoint(IceCandidate candidate, IIceSocket socket)
    {
        public IceCandidate Candidate { get; } = candidate;
        public IIceSocket Socket { get; } = socket;
        public Task? ReceiveTask { get; set; }
    }

    private enum PairState
    {
        Frozen,
        Waiting,
        InProgress,
        Succeeded,
        Failed,
    }

    private sealed class CandidatePair(IceAgent.LocalEndpoint local, IceCandidate remote, ulong priority)
    {
        public LocalEndpoint Local { get; } = local;
        public IceCandidate Remote { get; } = remote;
        public ulong Priority { get; } = priority;
        public PairState State { get; set; } = PairState.Waiting;
        public bool TriggeredCheck { get; set; }
        public bool NominationCheck { get; set; }
        public bool Nominated { get; set; }
        public bool RemoteRequestSeen { get; set; }
        public DateTime LastSentUtc { get; set; }
        public DateTime LastResponseUtc { get; set; }
        public int Transmits { get; set; }
    }
}
