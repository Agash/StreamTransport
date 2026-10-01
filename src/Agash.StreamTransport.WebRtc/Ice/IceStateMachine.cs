using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Agash.StreamTransport.WebRtc.Stun;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.WebRtc.Ice;

/// <summary>A datagram the state machine wants sent.</summary>
/// <param name="Local">The local endpoint to send from, by the handle it was added with.</param>
/// <param name="Destination">Where to send it.</param>
/// <param name="Data">The datagram.</param>
internal readonly record struct IceTransmit(int Local, IPEndPoint Destination, byte[] Data);

/// <summary>Something the state machine reports: a gathered candidate or a new state.</summary>
/// <param name="Candidate">A local candidate to trickle, or null.</param>
/// <param name="State">The new connection state, when <paramref name="Candidate"/> is null.</param>
internal readonly record struct IceEvent(IceCandidate? Candidate, IceConnectionState State);

/// <summary>
/// A full-ICE agent (RFC 8445) with trickle (RFC 8838), consent freshness (RFC 7675) and hot standby,
/// as a state machine with no sockets, threads or clock of its own. It is given datagrams and the time;
/// it produces datagrams to send, events, and when it next needs the time. Every call takes the time,
/// so tests drive it deterministically and the driver needs no more than a timer and its sockets. Not
/// thread-safe: the driver serialises calls.
/// </summary>
internal sealed partial class IceStateMachine
{
    private const int MaxCheckTransmits = 7;

    private readonly IceRole _role;
    private readonly IceTimings _timings;
    private readonly ILogger _logger;
    private readonly ulong _tieBreaker;
    private readonly List<LocalEndpoint> _locals = [];
    private readonly List<IceCandidate> _remoteCandidates = [];
    private readonly List<CandidatePair> _pairs = [];
    private readonly List<IPEndPoint> _stunServers = [];
    private readonly Dictionary<string, CandidatePair> _inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LocalEndpoint> _gatherInFlight = new(
        StringComparer.Ordinal
    );
    private readonly Queue<IceTransmit> _transmits = new();
    private readonly Queue<IceEvent> _events = new();
    private byte[] _localPassword;
    private IceCredentials _remote;
    private CandidatePair? _selected;
    private int _candidateIndex;

    public IceStateMachine(IceCredentials local, IceRole role, IceTimings timings, ILogger logger)
    {
        LocalCredentials = local;
        _localPassword = Encoding.UTF8.GetBytes(local.Password);
        _role = role;
        _timings = timings;
        _logger = logger;
        Span<byte> tieBreaker = stackalloc byte[8];
        RandomNumberGenerator.Fill(tieBreaker);
        _tieBreaker = BinaryPrimitives.ReadUInt64BigEndian(tieBreaker);
    }

    public IceCredentials LocalCredentials { get; private set; }

    public IceConnectionState State { get; private set; } = IceConnectionState.New;

    /// <summary>When the machine next needs <see cref="HandleTimeout"/>; null before it starts.</summary>
    public TimeSpan? NextTimeout { get; private set; }

    /// <summary>The selected pair: the local endpoint's handle and the remote endpoint; null when none.</summary>
    public (int Local, IPEndPoint Remote)? Selected =>
        _selected is { } pair ? (pair.Local.Handle, pair.Remote.Endpoint) : null;

    public void SetRemoteCredentials(IceCredentials remote) => _remote = remote;

    /// <summary>Server-reflexive queries still waiting for a response.</summary>
    public int PendingGathers => _gatherInFlight.Count;

    public void AddStunServer(IPEndPoint server) => _stunServers.Add(server);

    /// <summary>Adds a bound local endpoint as a candidate and pairs it with the known remotes.</summary>
    /// <param name="handle">The driver's handle for the endpoint's socket.</param>
    /// <param name="bound">The endpoint the socket sends from: its bound address, or a relayed address.</param>
    /// <param name="kind">Host, or relayed for a TURN allocation.</param>
    /// <param name="related">For a relayed candidate, the mapped address it was allocated from.</param>
    public void AddLocalEndpoint(
        int handle,
        IPEndPoint bound,
        IceCandidateKind kind = IceCandidateKind.Host,
        IPEndPoint? related = null
    )
    {
        uint priority = IceCandidate.ComputePriority(
            kind,
            bound.Address,
            IceCandidate.RtpComponent,
            _candidateIndex++
        );
        IceCandidate candidate = new(
            Foundation(kind, bound.Address),
            IceCandidate.RtpComponent,
            priority,
            bound,
            kind,
            related
        );
        LocalEndpoint local = new(handle, candidate);
        _locals.Add(local);
        foreach (IceCandidate remote in _remoteCandidates)
        {
            Pair(local, remote);
        }

        LogLocalCandidate(candidate.Kind, candidate.Endpoint);
        _events.Enqueue(new IceEvent(candidate, State));
    }

    /// <summary>Starts checking, and asks STUN servers for server-reflexive candidates.</summary>
    /// <param name="now">The time.</param>
    public void Start(TimeSpan now)
    {
        NextTimeout = now + _timings.Ta;
        SetState(IceConnectionState.Checking);
        Span<byte> transaction = stackalloc byte[StunHeader.TransactionIdLength];
        foreach (LocalEndpoint local in _locals)
        {
            if (local.Candidate.Kind != IceCandidateKind.Host)
            {
                continue;
            }

            foreach (IPEndPoint server in _stunServers)
            {
                if (!IceCandidate.CanReach(local.Candidate.Endpoint.Address, server.Address))
                {
                    continue;
                }

                RandomNumberGenerator.Fill(transaction);
                byte[] buffer = new byte[64];
                StunMessageWriter writer = new(
                    buffer,
                    StunMessageClass.Request,
                    StunMethod.Binding,
                    transaction
                );
                writer.AddFingerprint();
                _gatherInFlight[Convert.ToHexString(transaction)] = local;
                _transmits.Enqueue(new IceTransmit(local.Handle, server, buffer[..writer.Length]));
            }
        }
    }

    public void AddRemoteCandidate(IceCandidate candidate)
    {
        if (_remoteCandidates.Contains(candidate))
        {
            return;
        }

        _remoteCandidates.Add(candidate);
        foreach (LocalEndpoint local in _locals)
        {
            Pair(local, candidate);
        }

        LogRemoteCandidate(candidate.Kind, candidate.Endpoint);
    }

    /// <summary>Handles a STUN message that arrived on a local endpoint.</summary>
    /// <param name="local">The endpoint's handle.</param>
    /// <param name="source">Where it came from.</param>
    /// <param name="stun">The parsed message.</param>
    /// <param name="now">The time.</param>
    public void HandleStun(int local, IPEndPoint source, StunMessageReader stun, TimeSpan now)
    {
        if (_locals.Find(l => l.Handle == local) is not { } endpoint)
        {
            return;
        }

        switch (stun.Class)
        {
            case StunMessageClass.Request when stun.Method == StunMethod.Binding:
                HandleBindingRequest(stun, endpoint, source, now);
                break;
            case StunMessageClass.SuccessResponse when stun.Method == StunMethod.Binding:
                HandleBindingSuccess(stun, now);
                break;
            default:
                // Error responses and indications carry nothing the agent acts on.
                break;
        }
    }

    /// <summary>Runs the checks, consent and hot standby that are due.</summary>
    /// <param name="now">The time.</param>
    public void HandleTimeout(TimeSpan now)
    {
        if (NextTimeout is not { } tick || now < tick)
        {
            return;
        }

        // One pacing step per call; a driver that fell behind catches up at Ta, not in a burst.
        NextTimeout = tick + _timings.Ta > now ? tick + _timings.Ta : now + _timings.Ta;
        if (_remote.Password is not { Length: > 0 })
        {
            return;
        }

        SendNextCheck(now);
        MaintainConsent(now);
        MaintainHotStandby(now);
    }

    /// <summary>
    /// Re-probes every pair and re-nominates, for a network change or lost consent. The DTLS-SRTP
    /// session above is untouched: it is bound to the peer, not the path.
    /// </summary>
    public void TriggerRecovery()
    {
        _selected = null;
        foreach (CandidatePair pair in _pairs)
        {
            pair.State = PairState.Waiting;
            pair.Nominated = false;
            pair.NominationCheck = false;
            pair.NominatedByPeer = false;
            pair.TriggeredCheck = false;
            pair.Transmits = 0;
        }

        SetState(IceConnectionState.Checking);
        LogRecovery();
    }

    /// <summary>
    /// A full restart under new credentials: every local endpoint and pair goes; the driver adds freshly
    /// bound endpoints, which pair with the remote candidates already known.
    /// </summary>
    /// <param name="credentials">The new local credentials.</param>
    public void Restart(IceCredentials credentials)
    {
        LocalCredentials = credentials;
        _localPassword = Encoding.UTF8.GetBytes(credentials.Password);
        _selected = null;
        _inFlight.Clear();
        _pairs.Clear();
        _locals.Clear();
        SetState(IceConnectionState.Checking);
        LogRestart();
    }

    public bool TryPollTransmit(out IceTransmit transmit) => _transmits.TryDequeue(out transmit);

    public bool TryPollEvent(out IceEvent iceEvent) => _events.TryDequeue(out iceEvent);

    private static string Foundation(IceCandidateKind kind, IPAddress baseAddress) =>
        $"{(int)kind}-{baseAddress}";

    // How long ago something happened; forever when it never did.
    private static TimeSpan Since(TimeSpan? at, TimeSpan now) =>
        at is { } then ? now - then : TimeSpan.MaxValue;

    private void Pair(LocalEndpoint local, IceCandidate remote)
    {
        // Pairs are within a component, between addresses that reach each other.
        if (
            !IceCandidate.CanReach(local.Candidate.Endpoint.Address, remote.Endpoint.Address)
            || local.Candidate.ComponentId != remote.ComponentId
        )
        {
            return;
        }

        _pairs.Add(new CandidatePair(local, remote, PairPriority(local.Candidate, remote)));
        _pairs.Sort(static (a, b) => b.Priority.CompareTo(a.Priority));
    }

    private ulong PairPriority(IceCandidate local, IceCandidate remote) =>
        _role == IceRole.Controlling
            ? IceCandidate.ComputePairPriority(local.Priority, remote.Priority)
            : IceCandidate.ComputePairPriority(remote.Priority, local.Priority);

    private void HandleBindingRequest(
        StunMessageReader stun,
        LocalEndpoint local,
        IPEndPoint source,
        TimeSpan now
    )
    {
        // USERNAME is localUfrag:remoteUfrag, MESSAGE-INTEGRITY keyed by our password.
        if (!stun.VerifyMessageIntegrity(_localPassword))
        {
            return;
        }

        bool useCandidate = stun.TryFindAttribute(StunAttributeType.UseCandidate, out _);
        byte[] response = new byte[128];
        StunMessageWriter writer = new(
            response,
            StunMessageClass.SuccessResponse,
            StunMethod.Binding,
            stun.TransactionId
        );
        writer.AddXorMappedAddress(source);
        writer.AddMessageIntegrity(_localPassword);
        writer.AddFingerprint();
        _transmits.Enqueue(new IceTransmit(local.Handle, source, response[..writer.Length]));

        CandidatePair pair = PairForPeerReflexive(local, source);

        // An authenticated request proves the path alive in the receive direction now, whether or not our
        // own check was lost; consent then rides out loss that one direction's checks alone would not.
        pair.LastResponse = now;

        // A nomination can arrive before our own check on the pair succeeded; the controlling agent does
        // not repeat it, so it is remembered until that check succeeds (RFC 8445 section 7.3.1.5).
        if (useCandidate && _role == IceRole.Controlled)
        {
            if (pair.State == PairState.Succeeded)
            {
                Nominate(pair, now);
            }
            else
            {
                pair.NominatedByPeer = true;
            }
        }

        if (pair.State is PairState.Frozen or PairState.Failed)
        {
            pair.State = PairState.Waiting;
        }

        pair.TriggeredCheck = true;
    }

    private void HandleBindingSuccess(StunMessageReader stun, TimeSpan now)
    {
        string transaction = Convert.ToHexString(stun.TransactionId);
        if (_gatherInFlight.Remove(transaction, out LocalEndpoint? gathered))
        {
            HandleServerReflexive(stun, gathered);
            return;
        }

        if (!_inFlight.Remove(transaction, out CandidatePair? pair))
        {
            return;
        }

        // A response's MESSAGE-INTEGRITY is keyed by the responder's password.
        if (
            _remote.Password is { Length: > 0 }
            && !stun.VerifyMessageIntegrity(Encoding.UTF8.GetBytes(_remote.Password))
        )
        {
            return;
        }

        pair.State = PairState.Succeeded;
        pair.LastResponse = now;
        LogPairSucceeded(pair.Local.Candidate.Endpoint, pair.Remote.Endpoint);
        if (pair.NominationCheck || (pair.NominatedByPeer && _role == IceRole.Controlled))
        {
            Nominate(pair, now);
        }
        else if (_role == IceRole.Controlling && _selected is null)
        {
            // Regular nomination: the first valid pair, the highest-priority one checked, is nominated.
            pair.NominationCheck = true;
            pair.TriggeredCheck = true;
        }
    }

    private void HandleServerReflexive(StunMessageReader stun, LocalEndpoint local)
    {
        // No mapping, or none different from the host candidate: nothing new to offer.
        if (
            !stun.TryGetXorMappedAddress(out IPEndPoint mapped)
            || mapped.Equals(local.Candidate.Endpoint)
        )
        {
            return;
        }

        uint priority = IceCandidate.ComputePriority(
            IceCandidateKind.ServerReflexive,
            mapped.Address,
            IceCandidate.RtpComponent,
            _candidateIndex++
        );
        IceCandidate candidate = new(
            Foundation(IceCandidateKind.ServerReflexive, local.Candidate.Endpoint.Address),
            IceCandidate.RtpComponent,
            priority,
            mapped,
            IceCandidateKind.ServerReflexive,
            relatedAddress: local.Candidate.Endpoint
        );
        LogLocalCandidate(candidate.Kind, candidate.Endpoint);
        _events.Enqueue(new IceEvent(candidate, State));
    }

    private void Nominate(CandidatePair pair, TimeSpan now)
    {
        if (_selected is not null)
        {
            return;
        }

        pair.Nominated = true;
        pair.LastResponse = now;
        _selected = pair;
        LogSelectedPair(pair.Local.Candidate.Endpoint, pair.Remote.Endpoint);
        SetState(IceConnectionState.Connected);
    }

    private CandidatePair PairForPeerReflexive(LocalEndpoint local, IPEndPoint source)
    {
        foreach (CandidatePair existing in _pairs)
        {
            if (ReferenceEquals(existing.Local, local) && existing.Remote.Endpoint.Equals(source))
            {
                return existing;
            }
        }

        uint priority = IceCandidate.ComputePriority(
            IceCandidateKind.PeerReflexive,
            source.Address,
            IceCandidate.RtpComponent,
            _candidateIndex++
        );
        IceCandidate reflexive = new(
            Foundation(IceCandidateKind.PeerReflexive, source.Address),
            IceCandidate.RtpComponent,
            priority,
            source,
            IceCandidateKind.PeerReflexive
        );
        CandidatePair pair = new(local, reflexive, PairPriority(local.Candidate, reflexive));
        _pairs.Add(pair);
        _pairs.Sort(static (a, b) => b.Priority.CompareTo(a.Priority));
        return pair;
    }

    // Triggered checks first, then the highest-priority pair waiting or due a retransmission.
    private void SendNextCheck(TimeSpan now)
    {
        CandidatePair? check = _pairs.Find(static p => p.TriggeredCheck) ?? NextOrdinaryCheck(now);
        if (check is null)
        {
            return;
        }

        check.TriggeredCheck = false;

        // Re-checking a valid pair must not take it out of the valid list: a nomination arriving
        // meanwhile would find it not succeeded.
        if (check.State != PairState.Succeeded)
        {
            check.State = PairState.InProgress;
        }

        check.LastSent = now;
        check.Transmits++;
        SendBindingCheck(check);
    }

    private CandidatePair? NextOrdinaryCheck(TimeSpan now)
    {
        foreach (CandidatePair pair in _pairs)
        {
            if (pair.State is PairState.Frozen or PairState.Waiting)
            {
                return pair;
            }

            if (pair.State == PairState.InProgress && Since(pair.LastSent, now) > _timings.CheckRto)
            {
                if (pair.Transmits >= MaxCheckTransmits)
                {
                    pair.State = PairState.Failed;
                    continue;
                }

                return pair;
            }
        }

        return null;
    }

    // Consent is tracked on the selected pair: hot-standby pings on the alternates also bring responses,
    // so one timer across all pairs would never see the selected path die.
    private void MaintainConsent(TimeSpan now)
    {
        if (_selected is not { } selected)
        {
            return;
        }

        if (Since(selected.LastResponse, now) > _timings.ConsentTimeout)
        {
            LogConsentLost(selected.Remote.Endpoint);

            // A pre-warmed alternate takes over in one round trip; without one, every pair is re-probed.
            if (BestWarmAlternate(now, selected) is { } warm)
            {
                _selected = warm;
                warm.Nominated = true;
                LogSwitched(warm.Local.Candidate.Endpoint, warm.Remote.Endpoint);
                SetState(IceConnectionState.Connected);
            }
            else
            {
                TriggerRecovery();
            }

            return;
        }

        if (Since(selected.LastSent, now) > _timings.ConsentInterval)
        {
            selected.LastSent = now;
            SendBindingCheck(selected);
        }
    }

    // Keeps the other valid pairs warm at the consent cadence, so a failover promotes one in a round trip.
    private void MaintainHotStandby(TimeSpan now)
    {
        foreach (CandidatePair pair in _pairs)
        {
            if (
                !ReferenceEquals(pair, _selected)
                && pair.State == PairState.Succeeded
                && Since(pair.LastSent, now) > _timings.ConsentInterval
            )
            {
                pair.LastSent = now;
                SendBindingCheck(pair);
            }
        }
    }

    // The highest-priority valid alternate that answered within the consent window.
    private CandidatePair? BestWarmAlternate(TimeSpan now, CandidatePair exclude)
    {
        CandidatePair? best = null;
        foreach (CandidatePair pair in _pairs)
        {
            if (
                !ReferenceEquals(pair, exclude)
                && pair.State == PairState.Succeeded
                && Since(pair.LastResponse, now) <= _timings.ConsentTimeout
                && (best is null || pair.Priority > best.Priority)
            )
            {
                best = pair;
            }
        }

        return best;
    }

    private void SendBindingCheck(CandidatePair pair)
    {
        Span<byte> transaction = stackalloc byte[StunHeader.TransactionIdLength];
        RandomNumberGenerator.Fill(transaction);
        byte[] buffer = new byte[160];
        StunMessageWriter writer = new(
            buffer,
            StunMessageClass.Request,
            StunMethod.Binding,
            transaction
        );
        string username = IceCredentials.CheckUsername(
            _remote.UsernameFragment,
            LocalCredentials.UsernameFragment
        );
        writer.AddAttribute(StunAttributeType.Username, Encoding.UTF8.GetBytes(username));

        Span<byte> priority = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(
            priority,
            IceCandidate.ComputePriority(
                IceCandidateKind.PeerReflexive,
                pair.Local.Candidate.Endpoint.Address,
                IceCandidate.RtpComponent
            )
        );
        writer.AddAttribute(StunAttributeType.Priority, priority);

        Span<byte> tieBreaker = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(tieBreaker, _tieBreaker);
        writer.AddAttribute(
            _role == IceRole.Controlling
                ? StunAttributeType.IceControlling
                : StunAttributeType.IceControlled,
            tieBreaker
        );
        if (_role == IceRole.Controlling && pair.NominationCheck)
        {
            writer.AddAttribute(StunAttributeType.UseCandidate, default);
        }

        writer.AddMessageIntegrity(Encoding.UTF8.GetBytes(_remote.Password));
        writer.AddFingerprint();
        _inFlight[Convert.ToHexString(transaction)] = pair;
        _transmits.Enqueue(
            new IceTransmit(pair.Local.Handle, pair.Remote.Endpoint, buffer[..writer.Length])
        );
    }

    private void SetState(IceConnectionState state)
    {
        if (State != state)
        {
            State = state;
            _events.Enqueue(new IceEvent(null, state));
        }
    }

    [LoggerMessage(
        EventId = 1130,
        Level = LogLevel.Debug,
        Message = "ICE local candidate {Kind} {Endpoint}"
    )]
    private partial void LogLocalCandidate(IceCandidateKind kind, IPEndPoint endpoint);

    [LoggerMessage(
        EventId = 1131,
        Level = LogLevel.Debug,
        Message = "ICE remote candidate {Kind} {Endpoint}"
    )]
    private partial void LogRemoteCandidate(IceCandidateKind kind, IPEndPoint endpoint);

    [LoggerMessage(
        EventId = 1132,
        Level = LogLevel.Debug,
        Message = "ICE pair succeeded {Local} -> {Remote}"
    )]
    private partial void LogPairSucceeded(IPEndPoint local, IPEndPoint remote);

    [LoggerMessage(
        EventId = 1133,
        Level = LogLevel.Information,
        Message = "ICE selected pair {Local} -> {Remote}"
    )]
    private partial void LogSelectedPair(IPEndPoint local, IPEndPoint remote);

    [LoggerMessage(
        EventId = 1134,
        Level = LogLevel.Warning,
        Message = "ICE consent lost to {Remote}"
    )]
    private partial void LogConsentLost(IPEndPoint remote);

    [LoggerMessage(
        EventId = 1135,
        Level = LogLevel.Information,
        Message = "ICE recovery: re-probing candidate pairs (SRTP session preserved)"
    )]
    private partial void LogRecovery();

    [LoggerMessage(
        EventId = 1136,
        Level = LogLevel.Information,
        Message = "ICE switched to warm pair {Local} -> {Remote} (SRTP session preserved)"
    )]
    private partial void LogSwitched(IPEndPoint local, IPEndPoint remote);

    [LoggerMessage(
        EventId = 1137,
        Level = LogLevel.Information,
        Message = "ICE restart: re-gathering under fresh credentials (SRTP session preserved)"
    )]
    private partial void LogRestart();

    private sealed record LocalEndpoint(int Handle, IceCandidate Candidate);

    private enum PairState
    {
        Frozen,
        Waiting,
        InProgress,
        Succeeded,
        Failed,
    }

    private sealed class CandidatePair(LocalEndpoint local, IceCandidate remote, ulong priority)
    {
        public LocalEndpoint Local { get; } = local;

        public IceCandidate Remote { get; } = remote;

        public ulong Priority { get; } = priority;

        public PairState State { get; set; } = PairState.Waiting;

        public bool TriggeredCheck { get; set; }

        public bool NominationCheck { get; set; }

        public bool Nominated { get; set; }

        public bool NominatedByPeer { get; set; }

        public TimeSpan? LastSent { get; set; }

        public TimeSpan? LastResponse { get; set; }

        public int Transmits { get; set; }
    }
}
