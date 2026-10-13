using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Agash.StreamTransport.Stun;
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

    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    // Equally ranked pairs switch only for this much less round trip, as libwebrtc's kMinImprovement.
    private static readonly TimeSpan MinimumRoundTripImprovement = TimeSpan.FromMilliseconds(10);

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
    private readonly Dictionary<string, GatherRequest> _gatherInFlight = new(
        StringComparer.Ordinal
    );

    // RFC 8489 section 6.2.1: a request over UDP is sent again after RTO, doubling, up to Rc times, and given
    // up Rm times RTO after the last.
    private static readonly TimeSpan GatherRto = TimeSpan.FromMilliseconds(500);
    private const int GatherTransmits = 7;
    private const int GatherFinalWait = 16;
    private readonly Queue<IceTransmit> _transmits = new();
    private readonly Queue<IceEvent> _events = new();
    private byte[] _localPassword;
    private IceCredentials _remote;
    private CandidatePair? _selected;

    // When the agent last started checking with nothing selected, for the failure timeout; and when the
    // peer last trickled a candidate, so failed pairs fail the agent only once it has gone quiet.
    private TimeSpan? _checkingSince;
    private bool _renomination;
    private readonly Random _random;

    // Whether the selected pair was weak at the last look, and when it last became so.
    private bool _weak;
    private TimeSpan? _weakAt;
    private bool _nominationDone;
    private uint _nominationNumber;
    private TimeSpan _remoteCandidateAt;
    private int _remoteCandidatesSeen;
    private bool _started;

    // The pair the last data datagram came over, so the per-packet lookup is one comparison.
    private CandidatePair? _lastData;
    private int _candidateIndex;

    public IceStateMachine(
        IceCredentials local,
        IceRole role,
        IceTimings timings,
        ILogger logger,
        WebRtcMetrics? metrics = null,
        Random? random = null
    )
    {
        _metrics = metrics ?? WebRtcMetrics.Shared;
        _random = random ?? Random.Shared;
        LocalCredentials = local;
        _localPassword = Encoding.UTF8.GetBytes(local.Password);
        Role = role;
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

    /// <summary>
    /// Sets the peer's ICE credentials. New ones after the first come with an ICE restart, which restarts
    /// this agent too and forgets the peer's old candidates (<see cref="Restart"/>).
    /// </summary>
    /// <param name="remote">The peer's credentials.</param>
    public void SetRemoteCredentials(IceCredentials remote) => _remote = remote;

    /// <summary>
    /// Sets the peer's ICE options. With renomination on both sides the controlling agent may nominate
    /// another pair at any time; without it a pair is nominated once, and moving takes an ICE restart
    /// (RFC 8445 section 8.1.1).
    /// </summary>
    /// <param name="remote">The peer's options.</param>
    public void SetRemoteOptions(IceOptions remote) =>
        _renomination = remote.HasFlag(IceOptions.Renomination);

    /// <summary>
    /// Whether the agent can move media to another pair on its own: as the controlled agent it follows the
    /// controlling one; as the controlling agent before its first nomination or with renomination. When
    /// false, leaving the selected pair takes an ICE restart.
    /// </summary>
    public bool CanMovePath => MayNominate;

    /// <summary>
    /// How often the selected path was shown to carry packets both ways: each answer to a check on the
    /// selected pair, and each newly selected pair.
    /// </summary>
    public long PathConfirmations { get; private set; }

    // Whether this agent may nominate now: always as the controlled agent, which never does; as the
    // controlling one before its first nomination succeeded, or at any time with renomination.
    private bool MayNominate => Role == IceRole.Controlled || _renomination || !_nominationDone;

    // Numbers each nomination, so a controlled agent with renomination takes the latest whatever the order
    // its checks arrive in.
    private uint NextNomination() => _nominationNumber = (_nominationNumber % 0xFFFFFF) + 1;

    /// <summary>
    /// The best valid pair on another local endpoint than the selected one, writable and answering its
    /// keep-alives: a second, independent path (another interface or modem) repairs can travel over.
    /// Null when none.
    /// </summary>
    /// <param name="now">The time.</param>
    /// <returns>The pair's local handle and remote endpoint, or null.</returns>
    public (int Local, IPEndPoint Remote)? Standby(TimeSpan now)
    {
        if (_selected is not { } selected)
        {
            return null;
        }

        CandidatePair? best = null;
        foreach (CandidatePair pair in _pairs)
        {
            if (
                pair.State == PairState.Succeeded
                && pair.Local.Handle != selected.Local.Handle
                && IsWritable(pair, now)
                && Since(pair.LastResponse, now) <= _timings.StandbyPingInterval * 2
                && (best is null || Compare(pair, best, now, damped: false) > 0)
            )
            {
                best = pair;
            }
        }

        return best is null ? null : (best.Local.Handle, best.Remote.Endpoint);
    }

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
                .Where(entry => ReferenceEquals(entry.Value.Local, local))
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
            if (MayNominate && Best(now) is { } next && IsWritable(next, now))
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

        // RFC 8489 section 14.7: a message with a FINGERPRINT that does not match is not STUN.
        if (
            stun.TryFindAttribute(StunAttributeType.Fingerprint, out _) && !stun.VerifyFingerprint()
        )
        {
            return;
        }

        switch (stun.Class)
        {
            case StunMessageClass.Request when stun.Method == StunMethod.Binding:
                HandleBindingRequest(stun, endpoint, source, now);
                break;
            case StunMessageClass.SuccessResponse when stun.Method == StunMethod.Binding:
                HandleBindingSuccess(stun, endpoint, source, now);
                break;
            case StunMessageClass.ErrorResponse when stun.Method == StunMethod.Binding:
                HandleBindingError(stun, now);
                break;
            default:
                // Indications carry nothing the agent acts on.
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
        CheckForFailure(now);
        RetransmitGathers(now);
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
            else if (!pair.ConsentLost)
            {
                pair.State = PairState.Waiting;
                pair.Transmits = 0;
            }
        }

        LogRecovery();
    }

    /// <summary>
    /// A full restart under new credentials: every local endpoint, pair and remote candidate goes; the
    /// driver adds freshly bound endpoints, which pair with the remote candidates the restart brings.
    /// </summary>
    /// <param name="credentials">The new local credentials.</param>
    public void Restart(IceCredentials credentials)
    {
        LocalCredentials = credentials;
        _localPassword = Encoding.UTF8.GetBytes(credentials.Password);
        _selected = null;
        _lastData = null;
        _nominationDone = false;
        _inFlight.Clear();
        _pairs.Clear();
        _locals.Clear();

        // RFC 9725 section 4.3.3 and RFC 8839 section 4.4.1.1.1: after a restart the peer's candidates are
        // the new set its description and trickle bring; a candidate of the old session is discarded. One
        // the peer still answers from is learned again as peer-reflexive.
        _remoteCandidates.Clear();
        _remoteCandidatesSeen = 0;
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
            byte[] request = buffer[..writer.Length];
            _gatherInFlight[Convert.ToHexString(transaction)] = new GatherRequest(
                local,
                server,
                request
            );
            _transmits.Enqueue(new IceTransmit(local.Handle, server, request));
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
        Role == IceRole.Controlling
            ? IceCandidate.ComputePairPriority(local.Priority, remote.Priority)
            : IceCandidate.ComputePairPriority(remote.Priority, local.Priority);

    private void HandleBindingRequest(
        StunMessageReader stun,
        LocalEndpoint local,
        IPEndPoint source,
        TimeSpan now
    )
    {
        // RFC 8489 section 9.1.3 under ICE's short-term credentials (RFC 8445 section 7.3): no USERNAME or
        // MESSAGE-INTEGRITY is a 400; a USERNAME for another session (one from before a restart) or a
        // MESSAGE-INTEGRITY that does not verify is a 401. Neither answer can be authenticated.
        bool hasUsername = stun.TryFindAttribute(
            StunAttributeType.Username,
            out ReadOnlySpan<byte> username
        );
        if (!hasUsername || !stun.TryFindAttribute(StunAttributeType.MessageIntegrity, out _))
        {
            SendError(stun, local, source, 400, "Bad Request", authenticated: false);
            return;
        }

        if (!IsOurUsername(username) || !stun.VerifyMessageIntegrity(_localPassword))
        {
            SendError(stun, local, source, 401, "Unauthenticated", authenticated: false);
            return;
        }

        // RFC 8489 section 6.3.1: a comprehension-required attribute this agent does not know is a 420.
        Span<ushort> unknown = stackalloc ushort[8];
        int unknownCount = stun.CollectUnknownRequired(UnderstoodInChecks, unknown);
        if (unknownCount > 0)
        {
            SendError(
                stun,
                local,
                source,
                420,
                "Unknown Attribute",
                authenticated: true,
                unknown[..unknownCount]
            );
            return;
        }

        if (RoleConflict(stun))
        {
            SendRoleConflict(stun, local, source);
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

        // RFC 8445 section 7.3.1.3: a peer-reflexive candidate takes the priority its request carries.
        CandidatePair pair = PairForPeerReflexive(
            local,
            source,
            stun.TryGetUInt32(StunAttributeType.Priority, out uint requestPriority)
                ? requestPriority
                : null
        );

        // An authenticated request shows the pair receiving. Consent stays with responses to our own
        // checks (RFC 7675): a request shows only that the peer's packets reach us.
        pair.LastReceived = now;

        // A nomination can arrive before our own check on the pair succeeded, so it is remembered until
        // that check succeeds (RFC 8445 section 7.3.1.5). A nomination of another pair once one is
        // selected is the controlling agent moving: it ranks the pair above unnominated ones.
        if (useCandidate && Role == IceRole.Controlled)
        {
            pair.NominatedByPeer = true;
            pair.LastNominated = now;
            if (stun.TryGetUInt32(StunAttributeType.Nomination, out uint nomination))
            {
                pair.RemoteNomination = Math.Max(pair.RemoteNomination, nomination & 0xFFFFFF);
            }

            if (pair.State == PairState.Succeeded)
            {
                Nominate(pair, now);
            }
        }

        // A triggered check answers a request on a pair not yet valid (RFC 8445 section 7.3.1.4); on a valid
        // pair nothing further is done, as libwebrtc checks only pairs that are not writable. Answering
        // every request with a check would bounce checks between the agents at the pacing rate and leave
        // no room for the other pairs' checks.
        // A pair whose consent lapsed is not checked again under these credentials (RFC 7675 section 5.1).
        if (pair.State == PairState.Succeeded || pair.ConsentLost)
        {
            return;
        }

        if (pair.State is PairState.Frozen or PairState.Failed)
        {
            pair.State = PairState.Waiting;
        }

        pair.TriggeredCheck = true;
    }

    /// <summary>The agent's role now: the one it started with, or the one a role conflict settled on.</summary>
    public IceRole Role { get; private set; }

    /// <summary>
    /// Takes a role, as when the peer is an ICE lite agent, which leaves the full agent controlling
    /// (RFC 8445 section 6.1.1).
    /// </summary>
    /// <param name="role">The role.</param>
    public void SetRole(IceRole role)
    {
        if (role == Role)
        {
            return;
        }

        Role = role;
        foreach (CandidatePair pair in _pairs)
        {
            pair.Priority = PairPriority(pair.Local.Candidate, pair.Remote);
        }

        _pairs.Sort(static (a, b) => b.Priority.CompareTo(a.Priority));
        LogRoleChanged(role);
    }

    // RFC 8445 section 7.3.1.1: both agents claiming one role is settled by the tie-breakers. The agent with
    // the larger one keeps the role it claims, and answers 487 when that is its own role; the other agent
    // takes the opposite role and carries on. True when this request is answered with 487.
    private bool RoleConflict(StunMessageReader stun)
    {
        StunAttributeType theirs =
            Role == IceRole.Controlling
                ? StunAttributeType.IceControlling
                : StunAttributeType.IceControlled;
        if (!stun.TryFindAttribute(theirs, out ReadOnlySpan<byte> value) || value.Length != 8)
        {
            return false;
        }

        bool oursWins = _tieBreaker >= BinaryPrimitives.ReadUInt64BigEndian(value);
        if (Role == IceRole.Controlling)
        {
            if (oursWins)
            {
                return true;
            }

            SetRole(IceRole.Controlled);
            return false;
        }

        if (oursWins)
        {
            SetRole(IceRole.Controlling);
            return false;
        }

        return true;
    }

    // The attributes a connectivity check may carry (RFC 8445 section 7.1, RFC 8489, renomination).
    private static readonly StunAttributeType[] UnderstoodInChecks =
    [
        StunAttributeType.Username,
        StunAttributeType.MessageIntegrity,
        StunAttributeType.MessageIntegritySha256,
        StunAttributeType.Priority,
        StunAttributeType.UseCandidate,
    ];

    private void SendError(
        StunMessageReader stun,
        LocalEndpoint local,
        IPEndPoint source,
        int code,
        string reason,
        bool authenticated,
        scoped ReadOnlySpan<ushort> unknown = default
    )
    {
        byte[] response = new byte[128 + (unknown.Length * 2)];
        StunMessageWriter writer = new(
            response,
            StunMessageClass.ErrorResponse,
            StunMethod.Binding,
            stun.TransactionId
        );
        writer.AddErrorCode(code, reason);
        if (!unknown.IsEmpty)
        {
            writer.AddUnknownAttributes(unknown);
        }

        if (authenticated)
        {
            writer.AddMessageIntegrity(_localPassword);
        }

        writer.AddFingerprint();
        _transmits.Enqueue(new IceTransmit(local.Handle, source, response[..writer.Length]));
    }

    private void SendRoleConflict(StunMessageReader stun, LocalEndpoint local, IPEndPoint source)
    {
        byte[] response = new byte[128];
        StunMessageWriter writer = new(
            response,
            StunMessageClass.ErrorResponse,
            StunMethod.Binding,
            stun.TransactionId
        );
        writer.AddErrorCode(487, "Role Conflict");
        writer.AddMessageIntegrity(_localPassword);
        writer.AddFingerprint();
        _transmits.Enqueue(new IceTransmit(local.Handle, source, response[..writer.Length]));
    }

    // A 487 answer to our check (RFC 8445 section 7.2.5.1): the peer kept the role, so this agent takes the
    // other and checks the pair again.
    // An authenticated error answer to our check: 487, the peer kept the role, so this agent takes the other
    // and checks the pair again (RFC 8445 section 7.2.5.1); 403, the peer revoked consent for the pair
    // (RFC 7675 section 5.2), which is then never used again under these credentials.
    private void HandleBindingError(StunMessageReader stun, TimeSpan now)
    {
        string transaction = Convert.ToHexString(stun.TransactionId);
        if (
            !stun.TryFindAttribute(StunAttributeType.ErrorCode, out ReadOnlySpan<byte> error)
            || error.Length < 4
            || !_inFlight.TryGetValue(transaction, out (CandidatePair Pair, TimeSpan SentAt) check)
            || !stun.VerifyMessageIntegrity(Encoding.UTF8.GetBytes(_remote.Password))
        )
        {
            return;
        }

        int code = ((error[2] & 0x07) * 100) + error[3];
        if (code == 487)
        {
            _ = _inFlight.Remove(transaction);
            SetRole(Role == IceRole.Controlling ? IceRole.Controlled : IceRole.Controlling);
            check.Pair.State = PairState.Waiting;
            check.Pair.TriggeredCheck = true;
        }
        else if (code == 403)
        {
            _ = _inFlight.Remove(transaction);
            LoseConsent(check.Pair, now);
        }
    }

    private void HandleBindingSuccess(
        StunMessageReader stun,
        LocalEndpoint local,
        IPEndPoint source,
        TimeSpan now
    )
    {
        string transaction = Convert.ToHexString(stun.TransactionId);
        if (_gatherInFlight.Remove(transaction, out GatherRequest? gathered))
        {
            HandleServerReflexive(stun, gathered.Local);
            return;
        }

        if (!_inFlight.TryGetValue(transaction, out (CandidatePair Pair, TimeSpan SentAt) check))
        {
            return;
        }

        CandidatePair pair = check.Pair;

        // RFC 7675 section 5.1: an answer after consent expired does not re-establish it.
        if (pair.ConsentLost)
        {
            _ = _inFlight.Remove(transaction);
            return;
        }

        // RFC 8445 section 7.2.5.2.1: the response comes from where the check went, to where it left from;
        // one that does not is discarded, and the check may still be answered. Its MESSAGE-INTEGRITY is
        // keyed by the responder's password.
        if (
            !ReferenceEquals(pair.Local, local)
            || !pair.Remote.Endpoint.Equals(source)
            || (
                _remote.Password is { Length: > 0 }
                && !stun.VerifyMessageIntegrity(Encoding.UTF8.GetBytes(_remote.Password))
            )
        )
        {
            return;
        }

        _ = _inFlight.Remove(transaction);
        if (ReferenceEquals(pair, _selected))
        {
            PathConfirmations++;
        }

        pair.State = PairState.Succeeded;
        pair.LastResponse = now;
        pair.LastReceived = now;
        pair.Unanswered = 0;
        pair.NoteRoundTrip(now - check.SentAt);
        LogPairSucceeded(pair.Local.Candidate.Endpoint, pair.Remote.Endpoint);
        if (pair.NominationCheck || (pair.NominatedByPeer && Role == IceRole.Controlled))
        {
            Nominate(pair, now);

            // Several pairs can be marked for nomination before the first is selected; the others must stop
            // carrying USE-CANDIDATE, or each of their keep-alives moves the controlled agent's selection.
            if (!ReferenceEquals(pair, _selected))
            {
                pair.NominationCheck = false;
            }
        }
        else if (Role == IceRole.Controlling && _selected is null && MayNominate)
        {
            // Regular nomination: the first valid pair, the highest-priority one checked, is nominated.
            pair.NominationCheck = true;
            pair.NominationNumber = NextNomination();
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
        PathConfirmations++;
        _nominationDone |= Role == IceRole.Controlling;
        LogSelectedPair(pair.Local.Candidate.Endpoint, pair.Remote.Endpoint);
        _metrics.SelectedPaths.Add(
            1,
            WebRtcMetrics.LocalKind(pair.Local.Candidate.Kind),
            WebRtcMetrics.RemoteKind(pair.Remote.Kind),
            WebRtcMetrics.Family(pair.Remote.Endpoint.AddressFamily)
        );
        SetState(IceConnectionState.Connected);
    }

    private CandidatePair PairForPeerReflexive(
        LocalEndpoint local,
        IPEndPoint source,
        uint? requestPriority = null
    )
    {
        foreach (CandidatePair existing in _pairs)
        {
            if (ReferenceEquals(existing.Local, local) && existing.Remote.Endpoint.Equals(source))
            {
                return existing;
            }
        }

        uint priority =
            requestPriority
            ?? IceCandidate.ComputePriority(
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

    // Checks one valid pair whose check is due, the one waiting longest. The selected pair carries media, so
    // its checks are consent checks, every ConsentInterval randomised as RFC 7675 section 5.1 requires,
    // never faster; its liveness is the media arriving. The others carry none and are checked every
    // StandbyPingInterval, or every WeakPingInterval while the selected pair is weak, so a failover finds
    // them receiving.
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
                pair.ConsentPeriod ??= ConsentPeriod();
                interval = pair.ConsentPeriod.Value;
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
            due.ConsentPeriod = null;
            SendBindingCheck(due, now);
        }
    }

    private TimeSpan ConsentPeriod() =>
        _timings.ConsentInterval * (0.8 + (0.4 * _random.NextDouble()));

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

        LoseConsent(selected, now);
    }

    // RFC 7675 section 5.1: once consent to a pair is lost, these credentials are not used on it again; it
    // stays failed until a restart brings new ones. The selection moves to the best valid alternate with
    // consent, or every other pair is checked again.
    private void LoseConsent(CandidatePair pair, TimeSpan now)
    {
        pair.ConsentLost = true;
        pair.State = PairState.Failed;
        pair.NominationCheck = false;
        if (!ReferenceEquals(pair, _selected))
        {
            return;
        }

        LogConsentLost(pair.Remote.Endpoint);
        _metrics.ConsentLost.Add(1);
        _selected = null;
        if (
            MayNominate
            && Best(now) is { } next
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
            if (pair.ConsentLost)
            {
                continue;
            }

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
        bool weak = IsWeak(now);
        if (weak && !_weak)
        {
            _weakAt = now;
        }

        _weak = weak;
        if (
            _selected is not null
            && State is IceConnectionState.Connected or IceConnectionState.Disconnected
        )
        {
            SetState(weak ? IceConnectionState.Disconnected : IceConnectionState.Connected);
        }

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
            || (Role == IceRole.Controlling && !MayNominate)
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

        if (Role == IceRole.Controlled)
        {
            if (a.NominatedByPeer != b.NominatedByPeer)
            {
                return a.NominatedByPeer ? 1 : -1;
            }

            // With renomination the highest number wins, a number over none (draft-thatcher-ice-renomination
            // section 4); without it, the latest nomination.
            if (a.RemoteNomination != b.RemoteNomination)
            {
                return a.RemoteNomination.CompareTo(b.RemoteNomination);
            }

            // The controlling agent nominates its selected pair on every check of it, so the most recent
            // nomination is where it sends media; data decides only between pairs nominated alike, since
            // repairs may arrive on a second path.
            int nominated = (a.LastNominated ?? TimeSpan.MinValue).CompareTo(
                b.LastNominated ?? TimeSpan.MinValue
            );
            if (nominated != 0)
            {
                return nominated;
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
        PathConfirmations++;
        pair.Nominated = true;
        pair.LastSent ??= now;

        // The controlling agent nominates the new pair at once, so the controlled agent follows it.
        if (Role == IceRole.Controlling)
        {
            pair.NominationCheck = true;
            pair.NominationNumber = NextNomination();
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

    // A pair media arrives on is receiving while something arrived within ReceivingTimeout, so a silent
    // path is left in seconds. One without media hears only answers to its checks, spaced by the consent
    // or standby cadence, so it gets that cadence, at its longest, plus the same margin; except while the
    // selected pair is weak, when the others are checked fast and must answer within ReceivingTimeout to
    // count. An answer from before the selected pair last went weak counts no longer than that either, and a
    // check left unanswered for ReceivingTimeout outweighs an older answer, so a failover never lands on a
    // pair that died with the selected one.
    private bool IsReceiving(CandidatePair pair, TimeSpan now)
    {
        if (pair.Unanswered > 0 && Since(pair.UnansweredSince, now) > _timings.ReceivingTimeout)
        {
            return false;
        }

        TimeSpan idle =
            TimeSpan.FromTicks(
                Math.Max((_timings.ConsentInterval * 1.2).Ticks, _timings.StandbyPingInterval.Ticks)
            ) + _timings.ReceivingTimeout;
        bool carriesMedia = Since(pair.LastDataReceived, now) <= idle;
        bool probing = !ReferenceEquals(pair, _selected) && IsWeak(now);
        bool stale = pair.LastReceived < _weakAt;
        TimeSpan window = carriesMedia || probing || stale ? _timings.ReceivingTimeout : idle;
        return Since(pair.LastReceived, now) <= window;
    }

    private static bool IsWritable(CandidatePair pair, TimeSpan now) =>
        pair.State == PairState.Succeeded
        && !(pair.Unanswered >= UnwritableChecks && Since(pair.LastResponse, now) > WriteTimeout);

    // A request's USERNAME starts with our username fragment and a colon (RFC 8445 section 7.2.2).
    private bool IsOurUsername(ReadOnlySpan<byte> username)
    {
        string ours = LocalCredentials.UsernameFragment;
        return username.Length > ours.Length
            && username[ours.Length] == (byte)':'
            && Encoding.UTF8.GetString(username[..ours.Length]) == ours;
    }

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
            Role == IceRole.Controlling
                ? StunAttributeType.IceControlling
                : StunAttributeType.IceControlled,
            tieBreaker
        );
        // The controlling agent nominates on every check of the selected pair (libwebrtc's semi-aggressive
        // mode), so a controlled agent that missed the first nomination after a switch still sees one. Once a
        // pair is selected no other is nominated: a controlled agent follows every nomination it sees.
        if (
            Role == IceRole.Controlling
            && (
                _selected is null
                    ? pair.NominationCheck && MayNominate
                    : ReferenceEquals(pair, _selected)
            )
        )
        {
            writer.AddAttribute(StunAttributeType.UseCandidate, default);
            if (_renomination)
            {
                Span<byte> number = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(number, pair.NominationNumber);
                writer.AddAttribute(StunAttributeType.Nomination, number);
            }
        }

        writer.AddMessageIntegrity(Encoding.UTF8.GetBytes(_remote.Password));
        writer.AddFingerprint();
        if (pair.Unanswered++ == 0)
        {
            pair.UnansweredSince = now;
        }

        _inFlight[Convert.ToHexString(transaction)] = (pair, now);
        _transmits.Enqueue(
            new IceTransmit(pair.Local.Handle, pair.Remote.Endpoint, buffer[..writer.Length])
        );
    }

    private void RetransmitGathers(TimeSpan now)
    {
        if (_gatherInFlight.Count == 0)
        {
            return;
        }

        foreach ((string transaction, GatherRequest request) in _gatherInFlight.ToArray())
        {
            request.SentAt ??= now;
            TimeSpan wait =
                request.Transmits >= GatherTransmits
                    ? GatherRto * GatherFinalWait
                    : GatherRto * (1 << (request.Transmits - 1));
            if (now - request.SentAt.Value < wait)
            {
                continue;
            }

            if (request.Transmits >= GatherTransmits)
            {
                _ = _gatherInFlight.Remove(transaction);
                continue;
            }

            request.Transmits++;
            request.SentAt = now;
            _transmits.Enqueue(
                new IceTransmit(request.Local.Handle, request.Server, request.Message)
            );
        }
    }

    // A server-reflexive gather request: where it went, what it was, and its retransmissions.
    private sealed class GatherRequest(LocalEndpoint local, IPEndPoint server, byte[] message)
    {
        public LocalEndpoint Local { get; } = local;

        public IPEndPoint Server { get; } = server;

        public byte[] Message { get; } = message;

        public TimeSpan? SentAt { get; set; }

        public int Transmits { get; set; } = 1;
    }

    // RFC 8445 section 8.1.2: the agent fails when every pair has failed and no more candidates are coming,
    // which with trickle means the peer has gone quiet for a receiving timeout; and, as browsers do, when
    // checking has run past the failure timeout with nothing selected, which covers a peer that never sent
    // a candidate or a description at all.
    private void CheckForFailure(TimeSpan now)
    {
        if (_remoteCandidates.Count != _remoteCandidatesSeen)
        {
            _remoteCandidatesSeen = _remoteCandidates.Count;
            _remoteCandidateAt = now;
        }

        if (State != IceConnectionState.Checking || _selected is not null)
        {
            _checkingSince = null;
            return;
        }

        _checkingSince ??= now;
        bool exhausted =
            _pairs.Count > 0
            && _pairs.TrueForAll(static p => p.State == PairState.Failed)
            && now - _remoteCandidateAt >= _timings.ReceivingTimeout;
        // RFC 8863 section 4: not before the PAC timer, even with every pair failed, as a peer's checks may
        // still reveal a peer-reflexive candidate.
        if (now - _checkingSince.Value >= _timings.FailureTimeout)
        {
            LogFailed(_pairs.Count, exhausted);
            SetState(IceConnectionState.Failed);
        }
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

        public ulong Priority { get; set; } = priority;

        public PairState State { get; set; } = PairState.Waiting;

        public bool TriggeredCheck { get; set; }

        // Consent was lost or revoked: these credentials are not used on the pair again.
        public bool ConsentLost { get; set; }

        public bool NominationCheck { get; set; }

        public bool Nominated { get; set; }

        public bool NominatedByPeer { get; set; }

        public TimeSpan? LastSent { get; set; }

        // The last response to one of our checks: writability and consent.
        public TimeSpan? LastResponse { get; set; }

        // The last anything received over the pair, checks or data: whether it is receiving.
        public TimeSpan? LastReceived { get; set; }

        public TimeSpan? LastDataReceived { get; set; }

        // When the controlling agent last nominated the pair (USE-CANDIDATE).
        public TimeSpan? LastNominated { get; set; }

        // The highest NOMINATION number the controlling agent sent for the pair; zero for none.
        public uint RemoteNomination { get; set; }

        // The number this agent nominates the pair with, as the controlling agent with renomination.
        public uint NominationNumber { get; set; }

        // The current consent period of the selected pair, drawn once a check.
        public TimeSpan? ConsentPeriod { get; set; }

        public bool WasReceiving { get; set; }

        public TimeSpan? ReceivingChangedAt { get; set; }

        // Checks sent since the last response, and when the first of them went.
        public int Unanswered { get; set; }

        public TimeSpan? UnansweredSince { get; set; }

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

    [LoggerMessage(
        EventId = 1139,
        Level = LogLevel.Warning,
        Message = "ICE failed: {Pairs} candidate pairs, {Reason}"
    )]
    private partial void LogFailedCore(int pairs, string reason);

    private void LogFailed(int pairs, bool exhausted) =>
        LogFailedCore(
            pairs,
            exhausted
                ? "every one failed and the peer sent no more candidates"
                : "none selected within the failure timeout"
        );

    [LoggerMessage(EventId = 1140, Level = LogLevel.Information, Message = "ICE role is {Role}")]
    private partial void LogRoleChanged(IceRole role);
}
