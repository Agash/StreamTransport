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

    // A valid pair stops being writable after this many unanswered pings over at least WriteTimeout, as
    // libwebrtc's ice_unwritable_min_checks and ice_unwritable_timeout.
    private const int UnwritableChecks = 5;

    // A pair's round trip is stable after this many samples with at most one ping outstanding, as
    // libwebrtc's Connection::stable (RTT_RATIO + 1).
    private const int StableRoundTrips = 4;

    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    // Equally ranked pairs switch only for this much less round trip, as libwebrtc's kMinImprovement.
    private static readonly TimeSpan MinimumRoundTripImprovement = TimeSpan.FromMilliseconds(10);

    private readonly IceRole _role;
    private readonly IceTimings _timings;
    private readonly WebRtcMetrics _metrics;
    private readonly ILogger _logger;
    private readonly ulong _tieBreaker;
    private readonly List<LocalEndpoint> _locals = [];
    private readonly List<IceCandidate> _remoteCandidates = [];
    private readonly List<CandidatePair> _pairs = [];
    private readonly List<IPEndPoint> _stunServers = [];

    // Checks awaiting their response, with when each was sent: a response times its pair's round trip.
    private readonly Dictionary<string, (CandidatePair Pair, TimeSpan SentAt)> _inFlight = new(
        StringComparer.Ordinal
    );
    private readonly Dictionary<string, LocalEndpoint> _gatherInFlight = new(
        StringComparer.Ordinal
    );
    private readonly Queue<IceTransmit> _transmits = new();
    private readonly Queue<IceEvent> _events = new();
    private byte[] _localPassword;
    private IceCredentials _remote;
    private CandidatePair? _selected;
    private bool _started;

    // The pair the last data datagram came over, so the per-packet lookup is one comparison.
    private CandidatePair? _lastData;
    private int _candidateIndex;

    public IceStateMachine(
        IceCredentials local,
        IceRole role,
        IceTimings timings,
        ILogger logger,
        WebRtcMetrics? metrics = null
    )
    {
        _metrics = metrics ?? WebRtcMetrics.Shared;
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

    /// <summary>The selected pair's smoothed round trip from its checks, once one has been answered.</summary>
    public TimeSpan? SelectedRoundTrip => _selected?.RoundTrip;

    /// <summary>
    /// The selected pair: the local endpoint's handle, the remote endpoint and both candidates' kinds;
    /// null when none.
    /// </summary>
    public (
        int Local,
        IPEndPoint Remote,
        IceCandidateKind LocalKind,
        IceCandidateKind RemoteKind
    )? Selected =>
        _selected is { } pair
            ? (pair.Local.Handle, pair.Remote.Endpoint, pair.Local.Candidate.Kind, pair.Remote.Kind)
            : null;

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

        // An interface that came up mid-session (continual gathering): its server-reflexive address is
        // asked for now, and its pairs are checked with the next checks.
        if (_started)
        {
            QueryStunServers(local);
        }
    }

    /// <summary>Starts checking, and asks STUN servers for server-reflexive candidates.</summary>
    /// <param name="now">The time.</param>
    public void Start(TimeSpan now)
    {
        NextTimeout = now + _timings.Ta;
        SetState(IceConnectionState.Checking);
        _started = true;
        foreach (LocalEndpoint local in _locals)
        {
            QueryStunServers(local);
        }
    }

    /// <summary>
    /// Removes a local endpoint whose address went away, with its pairs. When the selected pair was one of
    /// them, the best valid alternate takes over at once; with none, every pair is checked again.
    /// </summary>
    /// <param name="handle">The endpoint's handle.</param>
    /// <param name="now">The time.</param>
    public void RemoveLocalEndpoint(int handle, TimeSpan now)
    {
        if (_locals.Find(l => l.Handle == handle) is not { } local)
        {
            return;
        }

        _ = _locals.Remove(local);
        _ = _pairs.RemoveAll(p => ReferenceEquals(p.Local, local));
        foreach (
            string transaction in _inFlight
                .Where(entry => ReferenceEquals(entry.Value.Pair.Local, local))
                .Select(entry => entry.Key)
                .ToList()
        )
        {
            _ = _inFlight.Remove(transaction);
        }

        foreach (
            string transaction in _gatherInFlight
                .Where(entry => ReferenceEquals(entry.Value, local))
                .Select(entry => entry.Key)
                .ToList()
        )
        {
            _ = _gatherInFlight.Remove(transaction);
        }

        if (_lastData is { } last && ReferenceEquals(last.Local, local))
        {
            _lastData = null;
        }

        LogLocalCandidateRemoved(local.Candidate.Kind, local.Candidate.Endpoint);
        if (_selected is { } selected && ReferenceEquals(selected.Local, local))
        {
            _selected = null;
            if (Best(now) is { } next && IsWritable(next, now))
            {
                Switch(next, now);
            }
            else
            {
                Recheck();
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

    /// <summary>
    /// Notes a media, DTLS or RTCP datagram that arrived on a local endpoint: the pair it came over is
    /// receiving, and on the controlled side the pair with the newest data is the one the controlling
    /// agent sends on.
    /// </summary>
    /// <param name="local">The endpoint's handle.</param>
    /// <param name="source">Where it came from.</param>
    /// <param name="now">The time.</param>
    public void NoteDataReceived(int local, IPEndPoint source, TimeSpan now)
    {
        if (
            _lastData is { } last
            && last.Local.Handle == local
            && last.Remote.Endpoint.Equals(source)
        )
        {
            last.LastReceived = now;
            last.LastDataReceived = now;
            return;
        }

        foreach (CandidatePair pair in _pairs)
        {
            if (pair.Local.Handle == local && pair.Remote.Endpoint.Equals(source))
            {
                pair.LastReceived = now;
                pair.LastDataReceived = now;
                _lastData = pair;
                return;
            }
        }
    }

    /// <summary>Runs the checks, keep-alives, consent and path switches that are due.</summary>
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
        SendNextKeepAlive(now);
        MaintainConsent(now);
        SwitchIfBetter(now);
    }

    /// <summary>
    /// Re-checks every pair at once, for a network change: valid pairs get a check now and failed ones are
    /// checked again. The selected pair keeps carrying media meanwhile; the agent moves off it only when
    /// another pair is valid and ranks higher, as libwebrtc does on a network event, so a change that
    /// left the path working (an IPv6 privacy address rotating, another interface coming up) costs
    /// nothing. The DTLS-SRTP session above is bound to the peer, not the path.
    /// </summary>
    public void TriggerRecovery()
    {
        foreach (CandidatePair pair in _pairs)
        {
            if (pair.State == PairState.Succeeded)
            {
                pair.TriggeredCheck = true;
            }
            else
            {
                pair.State = PairState.Waiting;
                pair.Transmits = 0;
            }
        }

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
        _lastData = null;
        _inFlight.Clear();
        _pairs.Clear();
        _locals.Clear();
        SetState(IceConnectionState.Checking);
        _metrics.Restarts.Add(1);
        LogRestart();
    }

    public bool TryPollTransmit(out IceTransmit transmit) => _transmits.TryDequeue(out transmit);

    public bool TryPollEvent(out IceEvent iceEvent) => _events.TryDequeue(out iceEvent);

    // Asks every STUN server the endpoint reaches for its server-reflexive address.
    private void QueryStunServers(LocalEndpoint local)
    {
        if (local.Candidate.Kind != IceCandidateKind.Host)
        {
            return;
        }

        Span<byte> transaction = stackalloc byte[StunHeader.TransactionIdLength];
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

        // An authenticated request shows the pair receiving. Consent stays with responses to our own
        // checks (RFC 7675): a request shows only that the peer's packets reach us.
        pair.LastReceived = now;

        // A nomination can arrive before our own check on the pair succeeded, so it is remembered until
        // that check succeeds (RFC 8445 section 7.3.1.5). A nomination of another pair once one is
        // selected is the controlling agent moving: it ranks the pair above unnominated ones.
        if (useCandidate && _role == IceRole.Controlled)
        {
            pair.NominatedByPeer = true;
            if (pair.State == PairState.Succeeded)
            {
                Nominate(pair, now);
            }
        }

        // A triggered check answers a request on a pair not yet valid (RFC 8445 section 7.3.1.4); on a valid
        // pair nothing further is done, as libwebrtc checks only pairs that are not writable. Answering
        // every request with a check would bounce checks between the agents at the pacing rate and leave
        // no room for the other pairs' checks.
        if (pair.State == PairState.Succeeded)
        {
            return;
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

        if (!_inFlight.Remove(transaction, out (CandidatePair Pair, TimeSpan SentAt) check))
        {
            return;
        }

        CandidatePair pair = check.Pair;

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
        pair.LastReceived = now;
        pair.Unanswered = 0;
        pair.NoteRoundTrip(now - check.SentAt);
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
        _metrics.SelectedPaths.Add(
            1,
            WebRtcMetrics.LocalKind(pair.Local.Candidate.Kind),
            WebRtcMetrics.RemoteKind(pair.Remote.Kind),
            WebRtcMetrics.Family(pair.Remote.Endpoint.AddressFamily)
        );
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
        SendBindingCheck(check, now);
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

    // Pings one valid pair whose keep-alive is due, the one waiting longest, at libwebrtc's cadence: the
    // selected pair every StablePingInterval once strong and stable, otherwise every WeakPingInterval;
    // the others every StandbyPingInterval, or every WeakPingInterval while the selected pair is weak, so
    // a failover finds them receiving. Responses also keep consent on the selected pair (RFC 7675).
    private void SendNextKeepAlive(TimeSpan now)
    {
        bool weak = IsWeak(now);
        CandidatePair? due = null;
        foreach (CandidatePair pair in _pairs)
        {
            if (pair.State != PairState.Succeeded)
            {
                continue;
            }

            TimeSpan interval;
            if (ReferenceEquals(pair, _selected))
            {
                interval =
                    !weak && IsStable(pair)
                        ? _timings.StablePingInterval
                        : _timings.WeakPingInterval;
            }
            else
            {
                interval = weak ? _timings.WeakPingInterval : _timings.StandbyPingInterval;
            }

            if (
                Since(pair.LastSent, now) >= interval
                && (due is null || pair.LastSent < due.LastSent)
            )
            {
                due = pair;
            }
        }

        if (due is not null)
        {
            due.LastSent = now;
            SendBindingCheck(due, now);
        }
    }

    // Consent is the selected pair's: past its timeout with no response to our checks, sending on it
    // stops (RFC 7675 section 5.1). The best valid alternate with consent takes over; without one, every
    // pair is checked again.
    private void MaintainConsent(TimeSpan now)
    {
        if (
            _selected is not { } selected
            || Since(selected.LastResponse, now) <= _timings.ConsentTimeout
        )
        {
            return;
        }

        LogConsentLost(selected.Remote.Endpoint);
        _metrics.ConsentLost.Add(1);
        _selected = null;
        selected.NominationCheck = false;
        if (
            Best(now) is { } next
            && IsWritable(next, now)
            && Since(next.LastResponse, now) <= _timings.ConsentTimeout
        )
        {
            Switch(next, now);
            return;
        }

        Recheck();
    }

    // With no pair selected, every pair is checked again from the start and nominated afresh.
    private void Recheck()
    {
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

    // Moves the selection to the best valid pair when it ranks above the selected one, as libwebrtc's
    // ShouldSwitchConnection: writable first, then receiving (held for SwitchingDelay on both sides), on
    // the controlled side the controlling agent's nomination and then where its data arrives, then
    // priority; equal pairs switch for a clearly lower round trip.
    private void SwitchIfBetter(TimeSpan now)
    {
        foreach (CandidatePair pair in _pairs)
        {
            bool receiving = IsReceiving(pair, now);
            if (receiving != pair.WasReceiving)
            {
                pair.WasReceiving = receiving;
                pair.ReceivingChangedAt = now;
            }
        }

        if (
            _selected is not { } selected
            || Best(now) is not { } best
            || ReferenceEquals(best, selected)
            || !IsWritable(best, now)
        )
        {
            return;
        }

        int comparison = Compare(selected, best, now, damped: true);
        if (
            comparison < 0
            || (
                comparison == 0
                && best.RoundTrip is { } candidate
                && selected.RoundTrip is { } current
                && candidate <= current - MinimumRoundTripImprovement
            )
        )
        {
            Switch(best, now);
        }
    }

    // The highest-ranked valid pair, the lower round trip among equals.
    private CandidatePair? Best(TimeSpan now)
    {
        CandidatePair? best = null;
        foreach (CandidatePair pair in _pairs)
        {
            if (pair.State != PairState.Succeeded)
            {
                continue;
            }

            if (best is null)
            {
                best = pair;
                continue;
            }

            int comparison = Compare(pair, best, now, damped: false);
            if (
                comparison > 0
                || (
                    comparison == 0
                    && (pair.RoundTrip ?? TimeSpan.MaxValue) < (best.RoundTrip ?? TimeSpan.MaxValue)
                )
            )
            {
                best = pair;
            }
        }

        return best;
    }

    // Positive when a ranks above b. Damped, a pair that is receiving outranks one that is not only once
    // both have held their receiving state for SwitchingDelay, so a pair that just went quiet or just
    // came back does not flip the selection.
    private int Compare(CandidatePair a, CandidatePair b, TimeSpan now, bool damped)
    {
        bool aWritable = IsWritable(a, now);
        if (aWritable != IsWritable(b, now))
        {
            return aWritable ? 1 : -1;
        }

        bool aReceiving = IsReceiving(a, now);
        if (
            aReceiving != IsReceiving(b, now)
            && (
                !damped
                || (
                    Since(a.ReceivingChangedAt, now) >= _timings.SwitchingDelay
                    && Since(b.ReceivingChangedAt, now) >= _timings.SwitchingDelay
                )
            )
        )
        {
            return aReceiving ? 1 : -1;
        }

        if (_role == IceRole.Controlled)
        {
            if (a.NominatedByPeer != b.NominatedByPeer)
            {
                return a.NominatedByPeer ? 1 : -1;
            }

            int data = (a.LastDataReceived ?? TimeSpan.MinValue).CompareTo(
                b.LastDataReceived ?? TimeSpan.MinValue
            );
            if (data != 0)
            {
                return data;
            }
        }

        return a.Priority.CompareTo(b.Priority);
    }

    private void Switch(CandidatePair pair, TimeSpan now)
    {
        if (_selected is { } previous)
        {
            previous.NominationCheck = false;
        }

        _selected = pair;
        pair.Nominated = true;
        pair.LastSent ??= now;

        // The controlling agent nominates the new pair at once, so the controlled agent follows it.
        if (_role == IceRole.Controlling)
        {
            pair.NominationCheck = true;
            pair.TriggeredCheck = true;
        }

        LogSwitched(pair.Local.Candidate.Endpoint, pair.Remote.Endpoint);
        _metrics.SelectedPaths.Add(
            1,
            WebRtcMetrics.LocalKind(pair.Local.Candidate.Kind),
            WebRtcMetrics.RemoteKind(pair.Remote.Kind),
            WebRtcMetrics.Family(pair.Remote.Endpoint.AddressFamily)
        );
        SetState(IceConnectionState.Connected);
    }

    private bool IsWeak(TimeSpan now) =>
        _selected is not { } selected || !IsWritable(selected, now) || !IsReceiving(selected, now);

    private bool IsReceiving(CandidatePair pair, TimeSpan now) =>
        Since(pair.LastReceived, now) <= _timings.ReceivingTimeout;

    private static bool IsWritable(CandidatePair pair, TimeSpan now) =>
        pair.State == PairState.Succeeded
        && !(pair.Unanswered >= UnwritableChecks && Since(pair.LastResponse, now) > WriteTimeout);

    private static bool IsStable(CandidatePair pair) =>
        pair.RoundTripSamples >= StableRoundTrips && pair.Unanswered <= 1;

    private void SendBindingCheck(CandidatePair pair, TimeSpan now)
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
        // The controlling agent nominates on every check of the selected pair (libwebrtc's semi-aggressive
        // mode), so a controlled agent that missed the first nomination after a switch still sees one.
        if (
            _role == IceRole.Controlling
            && (pair.NominationCheck || ReferenceEquals(pair, _selected))
        )
        {
            writer.AddAttribute(StunAttributeType.UseCandidate, default);
        }

        writer.AddMessageIntegrity(Encoding.UTF8.GetBytes(_remote.Password));
        writer.AddFingerprint();
        pair.Unanswered++;
        _inFlight[Convert.ToHexString(transaction)] = (pair, now);
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
        EventId = 1138,
        Level = LogLevel.Information,
        Message = "ICE local candidate {Kind} {Endpoint} removed: its address went away"
    )]
    private partial void LogLocalCandidateRemoved(IceCandidateKind kind, IPEndPoint endpoint);

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
        Message = "ICE re-checking candidate pairs (SRTP session preserved)"
    )]
    private partial void LogRecovery();

    [LoggerMessage(
        EventId = 1136,
        Level = LogLevel.Information,
        Message = "ICE switched to pair {Local} -> {Remote} (SRTP session preserved)"
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

        // The last response to one of our checks: writability and consent.
        public TimeSpan? LastResponse { get; set; }

        // The last anything received over the pair, checks or data: whether it is receiving.
        public TimeSpan? LastReceived { get; set; }

        public TimeSpan? LastDataReceived { get; set; }

        public bool WasReceiving { get; set; }

        public TimeSpan? ReceivingChangedAt { get; set; }

        // Checks sent since the last response.
        public int Unanswered { get; set; }

        public int RoundTripSamples { get; private set; }

        // The pair's smoothed round trip from its checks, as libwebrtc's Connection keeps it: each sample
        // weighs one to the estimate's three.
        public TimeSpan? RoundTrip { get; private set; }

        public void NoteRoundTrip(TimeSpan sample)
        {
            RoundTrip = RoundTrip is { } smoothed ? ((smoothed * 3) + sample) / 4 : sample;
            RoundTripSamples++;
        }

        public int Transmits { get; set; }
    }
}
