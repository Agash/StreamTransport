namespace Agash.StreamTransport.Adaptation;

/// <summary>Where a <see cref="PathMtuDiscovery"/> stands (RFC 8899 section 5.2).</summary>
public enum PathMtuState
{
    /// <summary>Confirming the path carries the base size, with a probe of that size.</summary>
    Base,

    /// <summary>Probing for a larger size than the one confirmed.</summary>
    Searching,

    /// <summary>The search is done; it starts again after the raise interval, or after a black hole's cooldown.</summary>
    SearchComplete,

    /// <summary>
    /// The path did not carry a probe of the base size. Packets stay at the base size, the smallest this
    /// transport uses, and the base probe is tried again after the black hole cooldown.
    /// </summary>
    Error,
}

/// <summary>The limits and timers of a <see cref="PathMtuDiscovery"/> (RFC 8899 section 5.1).</summary>
public sealed record PathMtuOptions
{
    /// <summary>
    /// BASE_PLPMTU, also MIN_PLPMTU: the size packets start at and never go below. 1200 bytes of UDP
    /// payload, as RFC 8899 recommends and QUIC requires (RFC 9000 section 14.3).
    /// </summary>
    public int BaseSize { get; init; } = 1200;

    /// <summary>
    /// MAX_PLPMTU, the largest size searched for: 1452 bytes, a 1500-byte Ethernet MTU less IPv6 and UDP
    /// headers, so a search stays within Ethernet on IPv4 and IPv6 alike.
    /// </summary>
    public int MaximumSize { get; init; } = 1452;

    /// <summary>The search stops once the next size would differ from the last probed by less than this.</summary>
    public int MinimumChange { get; init; } = 20;

    /// <summary>MAX_PROBES: how many times a size is probed before it counts as too large.</summary>
    public int MaximumProbes { get; init; } = 3;

    /// <summary>PMTU_RAISE_TIMER: how long a complete search rests before looking for a larger size.</summary>
    public TimeSpan RaiseInterval { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long after a black hole, or a failed base probe, before searching again. Congestion can look like
    /// a black hole, so the search returns well before the raise interval.
    /// </summary>
    public TimeSpan BlackHoleCooldown { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Checks the options are consistent.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(BaseSize, 576);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumSize, BaseSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(MinimumChange, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumProbes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RaiseInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(BlackHoleCooldown, TimeSpan.Zero);
    }
}

/// <summary>
/// Datagram packetization layer path MTU discovery (RFC 8899) for an acknowledged transport, apart from
/// any wire format. The transport asks <see cref="TakeProbe"/> whether to send a probe and of what size,
/// tells <see cref="OnProbeSent"/> the probe's packet number, and reports how every packet fared: the
/// probe acknowledged or lost by its own loss detection, as QUIC does (RFC 9000 section 14.3), so no probe
/// timer is needed; every other packet acknowledged or lost, for black hole detection. It sizes its packets
/// from <see cref="Current"/>.
/// </summary>
/// <remarks>
/// <para>
/// The search and the black hole detector follow quinn's (quinn-proto, <c>connection/mtud.rs</c>): a binary
/// search between the confirmed size and the maximum that probes the maximum itself last, one probe at a
/// time, each size probed up to <see cref="PathMtuOptions.MaximumProbes"/> times; and black holes judged
/// from loss bursts, runs of consecutively sent packets lost together. A burst is suspicious when every
/// packet in it is above the base size and no packet as large, sent after it, has been acknowledged: only
/// then could a smaller path MTU explain it. More than <see cref="BlackHoleThreshold"/> suspicious bursts
/// drop <see cref="Current"/> to the base size.
/// </para>
/// <para>
/// Before searching, a probe of the base size confirms the path carries it (the BASE state); when it
/// does not, the ERROR state holds the base size and tries again after the cooldown. Packet numbers increase
/// in sending order. Not thread-safe; the transport serialises calls.
/// </para>
/// </remarks>
public sealed class PathMtuDiscovery
{
    /// <summary>The most suspicious loss bursts that do not mean a black hole; one more does.</summary>
    public const int BlackHoleThreshold = 3;

    private readonly PathMtuOptions _options;
    private readonly BlackHoleDetector _blackHoles;

    // The search: its bounds, the size last probed, the probe out (and its packet number once sent), and
    // how many probes of the last size were lost.
    private int _lowerBound;
    private int _upperBound;
    private int _lastProbed;
    private long? _probeId;
    private int _lostProbes;

    // A size chosen but not sent yet, because the transport could not build a probe that large.
    private int? _pending;

    // When a complete search, or an error, next probes.
    private TimeSpan _resumeAt;

    /// <summary>Starts in the BASE state at the base size.</summary>
    /// <param name="options">The limits and timers; the defaults when null.</param>
    public PathMtuDiscovery(PathMtuOptions? options = null)
    {
        _options = options ?? new PathMtuOptions();
        _options.Validate();
        _blackHoles = new BlackHoleDetector(_options.BaseSize);
        Current = _options.BaseSize;
        EnterBase();
    }

    /// <summary>
    /// The largest packet confirmed to cross the path (the PLPMTU), in transport payload bytes, the size
    /// the transport's packets keep to.
    /// </summary>
    public int Current { get; private set; }

    /// <summary>Where the search stands.</summary>
    public PathMtuState State { get; private set; }

    /// <summary>Whether a probe is out, awaiting its acknowledgement or loss.</summary>
    public bool ProbeInFlight { get; private set; }

    /// <summary>
    /// The size to probe now, if a probe should go: none while one is out, none in a complete search or
    /// an error until it resumes, and none larger than <paramref name="largestProbe"/>, which a transport
    /// that builds probes by padding its own packets can only reach so far. The caller sends a probe of
    /// the size and reports its packet number to <see cref="OnProbeSent"/>.
    /// </summary>
    /// <param name="now">The time.</param>
    /// <param name="largestProbe">The largest probe the transport can build now.</param>
    /// <returns>The size; null when no probe should go.</returns>
    public int? TakeProbe(TimeSpan now, int largestProbe)
    {
        if (ProbeInFlight)
        {
            return null;
        }

        switch (State)
        {
            case PathMtuState.SearchComplete or PathMtuState.Error when now < _resumeAt:
                return null;
            case PathMtuState.SearchComplete:
                StartSearch();
                break;
            case PathMtuState.Error:
                EnterBase();
                break;
            default:
                break;
        }

        if ((_pending ?? ChooseSize(now)) is not { } size)
        {
            return null;
        }

        if (size > largestProbe)
        {
            // Not buildable now: the same size goes once a large enough packet is at hand.
            _pending = size;
            return null;
        }

        _pending = null;
        ProbeInFlight = true;
        _probeId = null;
        return size;
    }

    // The base probe, a lost probe again at the same size, or the search's next size; null when the
    // search is done.
    private int? ChooseSize(TimeSpan now)
    {
        if (State == PathMtuState.Base || (0 < _lostProbes && _lostProbes < _options.MaximumProbes))
        {
            return _lastProbed;
        }

        bool lastSucceeded = _lostProbes == 0;
        _lostProbes = 0;
        if (NextSize(lastSucceeded) is not { } next)
        {
            CompleteSearch(now + _options.RaiseInterval);
            return null;
        }

        _lastProbed = next;
        return next;
    }

    /// <summary>The probe <see cref="TakeProbe"/> asked for went out with this packet number.</summary>
    /// <param name="packetId">Its packet number.</param>
    public void OnProbeSent(long packetId)
    {
        if (ProbeInFlight)
        {
            _probeId = packetId;
        }
    }

    /// <summary>The probe <see cref="TakeProbe"/> asked for was not sent after all; it is asked for again.</summary>
    public void OnProbeAbandoned()
    {
        if (ProbeInFlight && _probeId is null)
        {
            ProbeInFlight = false;
            _pending = _lastProbed;
        }
    }

    /// <summary>A packet was acknowledged.</summary>
    /// <param name="packetId">Its packet number.</param>
    /// <param name="size">Its size.</param>
    /// <returns>Whether <see cref="Current"/> grew.</returns>
    public bool OnAcknowledged(long packetId, int size)
    {
        if (!ProbeInFlight || _probeId != packetId)
        {
            _blackHoles.OnNonProbeAcknowledged(packetId, size);
            return false;
        }

        ProbeInFlight = false;
        _probeId = null;
        _lostProbes = 0;
        _blackHoles.OnProbeAcknowledged(packetId, size);
        if (State == PathMtuState.Base)
        {
            // The base size is confirmed; the search starts from it.
            StartSearch();
            return false;
        }

        bool grew = _lastProbed > Current;
        Current = Math.Max(Current, _lastProbed);
        return grew;
    }

    /// <summary>
    /// A packet was declared lost. Once every loss of a batch is reported, call
    /// <see cref="CheckBlackHole"/>.
    /// </summary>
    /// <param name="packetId">Its packet number.</param>
    /// <param name="size">Its size.</param>
    /// <param name="now">The time.</param>
    public void OnLost(long packetId, int size, TimeSpan now)
    {
        if (!ProbeInFlight || _probeId != packetId)
        {
            _blackHoles.OnNonProbeLost(packetId, size);
            return;
        }

        ProbeInFlight = false;
        _probeId = null;
        _lostProbes++;
        if (State == PathMtuState.Base && _lostProbes >= _options.MaximumProbes)
        {
            // RFC 8899 section 5.2: the base size is not confirmed. Nothing smaller is used, so hold it and
            // try again later.
            State = PathMtuState.Error;
            _resumeAt = now + _options.BlackHoleCooldown;
        }
    }

    /// <summary>
    /// Ends the batch of losses reported since the last call, and drops <see cref="Current"/> to the base
    /// size when the evidence points to a black hole (RFC 8899 section 4.3). The search then rests for the
    /// cooldown, and confirms nothing larger until it searches again.
    /// </summary>
    /// <param name="now">The time.</param>
    /// <returns>Whether a black hole was detected.</returns>
    public bool CheckBlackHole(TimeSpan now)
    {
        if (!_blackHoles.BlackHoleDetected())
        {
            return false;
        }

        Current = _options.BaseSize;
        CompleteSearch(now + _options.BlackHoleCooldown);
        return true;
    }

    /// <summary>
    /// The transport moved to another path, whose MTU is unknown: back to the base size, confirming it and
    /// searching afresh.
    /// </summary>
    /// <returns>Whether <see cref="Current"/> dropped.</returns>
    public bool OnPathChanged()
    {
        bool dropped = Current > _options.BaseSize;
        Current = _options.BaseSize;
        _blackHoles.Reset();
        EnterBase();
        return dropped;
    }

    private void EnterBase()
    {
        _pending = null;
        State = PathMtuState.Base;
        _lastProbed = _options.BaseSize;
        _lostProbes = 0;
        ProbeInFlight = false;
        _probeId = null;
    }

    private void StartSearch()
    {
        _pending = null;
        State = PathMtuState.Searching;
        _lowerBound = Current;
        _upperBound = Math.Max(_options.MaximumSize, Current);
        _lastProbed = Current;
        _lostProbes = 0;
    }

    private void CompleteSearch(TimeSpan resumeAt)
    {
        _pending = null;
        State = PathMtuState.SearchComplete;
        _resumeAt = resumeAt;
        ProbeInFlight = false;
        _probeId = null;
        _lostProbes = 0;
    }

    // Binary search: halve the range after each result, and probe the upper bound itself last when the
    // halving would stop short of it.
    private int? NextSize(bool lastSucceeded)
    {
        if (lastSucceeded)
        {
            _lowerBound = _lastProbed;
        }
        else
        {
            _upperBound = _lastProbed - 1;
        }

        int next = (_lowerBound + _upperBound) / 2;
        if (Math.Abs(next - _lastProbed) < _options.MinimumChange)
        {
            return _upperBound - _lastProbed >= _options.MinimumChange ? _upperBound : null;
        }

        return next;
    }

    // Loss bursts, and how many could be explained by a smaller path MTU.
    private sealed class BlackHoleDetector
    {
        private readonly int _baseSize;
        private readonly List<int> _suspicious = new(BlackHoleThreshold + 1);

        // The burst being gathered: its newest packet and smallest size.
        private long _burstNewest = -1;
        private int _burstSmallest;

        // The newest acknowledged packet as large as any acknowledged since the last suspicious burst, and
        // that size (the base size when none).
        private long _largestAfterLoss;
        private int _acknowledgedSize;

        public BlackHoleDetector(int baseSize)
        {
            _baseSize = baseSize;
            _acknowledgedSize = baseSize;
        }

        public void Reset()
        {
            _suspicious.Clear();
            _burstNewest = -1;
            _largestAfterLoss = 0;
            _acknowledgedSize = _baseSize;
        }

        public void OnProbeAcknowledged(long id, int size)
        {
            // A probe is larger than anything sent before it, so no earlier burst stays suspicious.
            _suspicious.Clear();
            _acknowledgedSize = size;
            _largestAfterLoss = id;
        }

        public void OnNonProbeAcknowledged(long id, int size)
        {
            if (size < _acknowledgedSize)
            {
                return;
            }

            if (size == _acknowledgedSize)
            {
                // The path still carries this size, so bursts sent before this packet are explained.
                _largestAfterLoss = Math.Max(_largestAfterLoss, id);
                return;
            }

            _acknowledgedSize = size;
            _largestAfterLoss = id;
            _ = _suspicious.RemoveAll(smallest => smallest <= size);
        }

        public void OnNonProbeLost(long id, int size)
        {
            if (_burstNewest >= 0 && id - _burstNewest != 1)
            {
                FinishBurst();
            }

            _burstSmallest = _burstNewest >= 0 ? Math.Min(_burstSmallest, size) : size;
            _burstNewest = id;
        }

        public bool BlackHoleDetected()
        {
            FinishBurst();
            if (_suspicious.Count <= BlackHoleThreshold)
            {
                return false;
            }

            _suspicious.Clear();
            return true;
        }

        private void FinishBurst()
        {
            if (_burstNewest < 0)
            {
                return;
            }

            long newest = _burstNewest;
            int smallest = _burstSmallest;
            _burstNewest = -1;

            // A burst holding a packet no larger than the base, or one sent before an acknowledged packet at
            // least as large, is not explained by a smaller MTU.
            if (
                smallest <= _baseSize
                || (newest < _largestAfterLoss && smallest <= _acknowledgedSize)
            )
            {
                return;
            }

            // A suspicious burst newer than the acknowledged packet makes that packet's size doubtful.
            if (newest > _largestAfterLoss)
            {
                _acknowledgedSize = _baseSize;
            }

            if (_suspicious.Count <= BlackHoleThreshold)
            {
                _suspicious.Add(smallest);
                return;
            }

            // Bounded: keep the most suspicious bursts, those with the largest smallest packet.
            int least = 0;
            for (int i = 1; i < _suspicious.Count; i++)
            {
                if (_suspicious[i] < _suspicious[least])
                {
                    least = i;
                }
            }

            if (_suspicious[least] < smallest)
            {
                _suspicious[least] = smallest;
            }
        }
    }
}
