namespace Agash.StreamTransport.Adaptation;

/// <summary>
/// SCReAMv2 (draft-ietf-ccwg-rfc8298bis-screamv2-01): a reference window bounds the bytes in flight and
/// sets the target rate (8 ref_wnd / s_rtt), reduced at most once per min(VIRTUAL_RTT, s_rtt) on
/// congestion and grown continuously, multiplicatively after congestion-free round trips. Loss backs off by
/// a fixed factor once the average loss rate shows it is not link-layer noise (section 4.5.2), classic ECN
/// by a smaller one, and L4S marks and queue delay (pseudo-L4S) in proportion to how much congestion they
/// show. Queue delay is the one-way delay above its base, as LEDBAT measures it (RFC 6817); a transport
/// that reports no arrival times gives the round trip above its floor instead.
/// </summary>
/// <remarks>
/// Section numbers refer to the draft. Where the draft leaves a detail open, the reference implementation
/// (EricssonResearch/scream, <c>ScreamV2Tx.cpp</c>) decides it: the 50 ms slow update, the history of bytes
/// in flight near the maximum rate, and releasing a window that no feedback has opened for half a second.
/// Not thread-safe: the transport serializes calls.
/// </remarks>
public sealed class ScreamCongestionController : ICongestionController
{
    // Section 4.2.1.3.
    private const double L4sAvgGUp = 1.0 / 8;
    private const double L4sAvgGDown = 1.0 / 128;

    // Section 4.2.1.4.
    private const double QdelayAvgG = 1.0 / 4;
    private const double QdelayMinMaxAvgG = 1.0 / 16;
    private const double QdelayDevAvgG = 1.0 / 32;
    private const double QdelayDevThreshold = 0.01;

    // Sections 4.2.2 and 4.3.
    private const double BytesInFlightHeadroom = 1.5;
    private const double RefWndOverheadMin = 1.5;
    private const double RefWndOverheadMax = 3.0;
    private const double RatePaceMin = 50_000;
    private const double RelaxedPacingLimitLow = 0.8;

    // Section 4.4.
    private const int PacketOverhead = 20;
    private const double RateAdjustGain = 1.0 / 16;
    private const double FrameSizeDevAlpha = 1.0 / 64;

    // Section 4.5.
    private const double QdelayMinAvgAlpha = 1.0 / 256;
    private const double LossRateThresholdPolicer = 0.1;
    private const double BetaLossPolicer = 0.9;
    private const double AckedBitrateMargin = 0.8;
    private const double BackoffScaleLowTargetRate = 0.25;

    // LEDBAT's base delay history: one minimum per minute, ten minutes (RFC 6817 section 2.4.2).
    private const int BaseHistory = 10;
    private const double BaseInterval = 60;

    // RFC 9331 section 4.3 item 3: CE marks arriving with a standing queue of at least this much come from a
    // classic ECN AQM (PIE, CoDel and RED mark at 5 to 20 ms); an L4S AQM marks at about a millisecond.
    private const double ClassicMarkingQueueDelay = 0.005;
    private const double ScalableMarkingQueueDelay = 0.002;
    private const int ClassicVerdicts = 3;

    // From the reference implementation.
    private const double SlowUpdateInterval = 0.05;
    private const double DeadlockInterval = 0.5;
    private const int QdelayNormHistory = 200;
    private const int QdelayNormShortHistory = 50;

    private readonly ScreamOptions _options;
    private readonly double _virtualRtt;
    private readonly double _qdelayTargetLo;
    private readonly double _qdelayTargetHi;
    private readonly Queue<(long Id, int Size)> _inFlight = new();
    private readonly double[] _baseDelays = new double[BaseHistory];
    private readonly Queue<double> _qdelayNorm = new();

    // Section 4.1.1.
    private long _maxBytesInFlight;
    private long _maxBytesInFlightPrev;
    private double _lastTransmit = double.NaN;
    private double _rttRolledAt;

    // Section 4.1.2.
    private long _highestAcked = long.MinValue;
    private long _bytesNewlyAcked;
    private long _bytesNewlyAckedCe;

    // Section 4.2.1.
    private bool _lossDetected;
    private bool _unitsMarked;
    private double _lossRate;
    private double _l4sAlpha;
    private double _lastL4sUpdate;
    private long _unitsDeliveredThisRtt;
    private long _unitsMarkedThisRtt;
    private double _qdelay;
    private double _qdelayAvg;
    private double _qdelayMaxAvg;
    private double _qdelayMinAvg;
    private double _qdelayDevAvg;
    private double _refWndDelayScale = 1;
    private double _lastQdelayAvgUpdate;

    // Section 4.2.2.
    private double _refWndI = 1;
    private bool _refWndIUpdateAllowed = true;
    private double _qdelayTarget;
    private double _lastCongestionDetected = double.NegativeInfinity;
    private double _lastReaction;
    private double _maxPolicedRefWnd = double.PositiveInfinity;

    // ECN response.
    private int _classicScore;

    // Round trip.
    private double _sRtt;
    private double _minRtt = double.PositiveInfinity;

    // Base delay (RFC 6817).
    private int _baseIndex;
    private double _baseRolledAt = double.NaN;

    // Section 4.4.
    private double _targetBitrate;
    private double _rateAdjustFactor;
    private double _frameSizeDev;
    private double _framePeriod = 0.02;
    private double _lastFrame = double.NaN;

    // Section 4.5.
    private double _qdelayMin = double.PositiveInfinity;
    private double _delayMinAvg;
    private double _driftHoldUntil = double.NegativeInfinity;
    private double _ackedBitrate;
    private double _ackedSince = double.NaN;
    private long _ackedBytes;
    private double _lossEventRate;
    private double _lastSlowUpdate;

    /// <summary>A controller with the given tunables, or the draft's recommended values.</summary>
    /// <param name="options">The tunables.</param>
    public ScreamCongestionController(ScreamOptions? options = null)
    {
        _options = options ?? new ScreamOptions();
        _virtualRtt = _options.VirtualRttMs / 1000.0;
        _qdelayTargetLo = _options.QueueDelayTargetMs / 1000.0;
        _qdelayTargetHi = Math.Max(_qdelayTargetLo, _options.QueueDelayTargetMaxMs / 1000.0);
        _qdelayTarget = _qdelayTargetLo;
        _qdelayMaxAvg = _qdelayTargetLo;
        EcnMode = _options.Ecn;
        Array.Fill(_baseDelays, double.PositiveInfinity);

        // The window the start rate needs over a nominal round trip.
        ReferenceWindow = Math.Max(
            _options.MinReferenceWindow,
            _options.StartBitrateBps / 8.0 * 0.1
        );
        _targetBitrate = Math.Clamp(
            _options.StartBitrateBps,
            _options.MinBitrateBps,
            _options.MaxBitrateBps
        );
        Current = Estimate();
    }

    /// <inheritdoc/>
    public CapacityEstimate Current { get; private set; }

    /// <inheritdoc/>
    public EcnCodepoint Ecn =>
        EcnMode switch
        {
            EcnMode.L4s => EcnCodepoint.Ect1,
            EcnMode.Classic => EcnCodepoint.Ect0,
            _ => EcnCodepoint.NotEct,
        };

    /// <summary>
    /// How CE marks are answered now: as configured, or classic once a classic ECN AQM was detected on a
    /// path configured for L4S.
    /// </summary>
    public EcnMode EcnMode { get; private set; }

    /// <inheritdoc/>
    public void UseEcn(EcnCodepoint codepoint)
    {
        EcnMode = codepoint switch
        {
            EcnCodepoint.Ect1 when _options.Ecn == EcnMode.L4s => EcnMode.L4s,
            EcnCodepoint.Ect1 or EcnCodepoint.Ect0 when _options.Ecn != EcnMode.None =>
                EcnMode.Classic,
            _ => _options.Ecn == EcnMode.None ? EcnMode.None : EcnMode,
        };
        _classicScore = 0;
    }

    /// <summary>The reference window, bytes.</summary>
    public double ReferenceWindow { get; private set; }

    /// <summary>The bytes sent and not yet acknowledged.</summary>
    public long BytesInFlight { get; private set; }

    /// <summary>The queue delay target now; it rises from its floor only when loss-based flows compete.</summary>
    public TimeSpan QueueDelayTarget => TimeSpan.FromSeconds(_qdelayTarget);

    /// <inheritdoc/>
    public void OnPacketSent(in SentPacket packet)
    {
        _inFlight.Enqueue((packet.Id, packet.Size));
        BytesInFlight += packet.Size;
        _maxBytesInFlight = Math.Max(_maxBytesInFlight, BytesInFlight);
        _lastTransmit = Seconds(packet.SentAt);
    }

    /// <inheritdoc/>
    public bool CanTransmit(int size, TimeSpan now)
    {
        double t = Seconds(now);

        // Nothing sent for half a second while the window stayed closed: the feedback that would open it is
        // not coming, so what is in flight is forgotten and sending resumes (reference implementation).
        if (!double.IsNaN(_lastTransmit) && t - _lastTransmit > DeadlockInterval)
        {
            _inFlight.Clear();
            BytesInFlight = 0;
            _lastTransmit = t;
        }

        // Section 4.3.1: bytes in flight around the reference window, with headroom that shrinks as delay
        // varies; at least two segments, so acknowledgement clocking never stalls.
        double overhead =
            RefWndOverheadMin + ((RefWndOverheadMax - RefWndOverheadMin) * _refWndDelayScale);
        double window =
            (Math.Max(ReferenceWindow, 2.0 * _options.MaxSegmentSize) * overhead)
            + _options.MaxSegmentSize;
        return BytesInFlight + size <= window;
    }

    /// <inheritdoc/>
    public void OnMediaFrame(int bytes, TimeSpan queueDelay, TimeSpan now)
    {
        double t = Seconds(now);
        if (!double.IsNaN(_lastFrame) && t > _lastFrame)
        {
            _framePeriod = Math.Clamp((_framePeriod * 0.9) + ((t - _lastFrame) * 0.1), 0.001, 1.0);
        }

        _lastFrame = t;

        // Section 4.4: an integrating controller on the sender queue's delay, and the frames' positive
        // deviation from their nominal size.
        double error = (queueDelay.TotalSeconds - (_framePeriod / 4)) / _framePeriod;
        _rateAdjustFactor = Math.Clamp(_rateAdjustFactor + (error * RateAdjustGain), 0, 0.5);
        double nominal = _targetBitrate * _framePeriod / 8;
        double deviation = nominal > 0 ? Math.Max(0, (bytes - nominal) / nominal) : 0;
        _frameSizeDev = Math.Min(
            0.2,
            ((1 - FrameSizeDevAlpha) * _frameSizeDev) + (FrameSizeDevAlpha * deviation)
        );
    }

    /// <inheritdoc/>
    public CapacityEstimate OnFeedback(
        ReadOnlySpan<PacketObservation> observations,
        TimeSpan? roundTrip,
        TimeSpan now
    )
    {
        double t = Seconds(now);
        if (roundTrip is { } rtt && rtt > TimeSpan.Zero)
        {
            double sample = rtt.TotalSeconds;
            _sRtt = _sRtt == 0 ? sample : (0.875 * _sRtt) + (0.125 * sample);
            _minRtt = Math.Min(_minRtt, sample);
        }

        double? owd = null;
        long highest = _highestAcked;
        foreach (PacketObservation observation in observations)
        {
            bool lost = observation.Outcome == PacketOutcome.Lost;

            // Section 4.5.2: the average loss rate, one sample a packet.
            double alpha = Math.Min(
                _options.LossRateThreshold / 4,
                _options.MaxSegmentSize / Math.Max(ReferenceWindow, 1) / 2
            );
            _lossRate = ((1 - alpha) * _lossRate) + (lost ? alpha : 0);
            if (lost)
            {
                _lossDetected = true;
                continue;
            }

            highest = Math.Max(highest, observation.Packet.Id);
            _unitsDeliveredThisRtt++;
            if (observation.Ecn == EcnCodepoint.Ce)
            {
                _unitsMarkedThisRtt++;
                _unitsMarked = true;
                _bytesNewlyAckedCe += observation.Packet.Size;
            }

            if (observation.ArrivedAt is { } arrived)
            {
                // The two clocks differ by a constant, which the base delay takes out.
                owd = (arrived - observation.Packet.SentAt).TotalSeconds;
            }
        }

        Acknowledge(highest, t);
        UpdateQueueDelay(owd, t);
        UpdateL4sAlpha(t);
        if (t - _lastReaction >= Math.Min(_virtualRtt, SmoothedRtt))
        {
            UpdateReferenceWindow(t);
        }

        SlowUpdate(t);
        UpdateTarget(t);
        return Current;
    }

    /// <inheritdoc/>
    public CapacityEstimate OnTick(TimeSpan now)
    {
        double t = Seconds(now);
        SlowUpdate(t);
        UpdateTarget(t);
        return Current;
    }

    private double SmoothedRtt => _sRtt > 0 ? _sRtt : _virtualRtt;

    private static double Seconds(TimeSpan time) => time.TotalSeconds;

    // Section 4.1: everything up to the highest acknowledged unit leaves flight, lost units included.
    private void Acknowledge(long highest, double t)
    {
        if (highest <= _highestAcked)
        {
            return;
        }

        _highestAcked = highest;
        while (_inFlight.TryPeek(out (long Id, int Size) sent) && sent.Id <= highest)
        {
            _ = _inFlight.Dequeue();
            BytesInFlight = Math.Max(0, BytesInFlight - sent.Size);
            _bytesNewlyAcked += sent.Size;
            _ackedBytes += sent.Size;
        }

        // Section 4.5.3: the acknowledged bitrate, averaged over about a round trip.
        if (double.IsNaN(_ackedSince))
        {
            _ackedSince = t;
        }
        else if (t - _ackedSince >= Math.Max(SmoothedRtt, 2 * _framePeriod))
        {
            double rate = _ackedBytes * 8 / (t - _ackedSince);
            _ackedBitrate = _ackedBitrate == 0 ? rate : (0.5 * _ackedBitrate) + (0.5 * rate);
            _ackedBytes = 0;
            _ackedSince = t;
        }
    }

    // Section 4.2.1.4, with the base delay of RFC 6817 and the clock drift guard of section 4.5.1.
    private void UpdateQueueDelay(double? owd, double t)
    {
        double? sample = null;
        if (owd is { } delay)
        {
            if (double.IsNaN(_baseRolledAt))
            {
                _baseRolledAt = t;
            }
            else if (t - _baseRolledAt >= BaseInterval)
            {
                _baseRolledAt = t;
                _baseIndex = (_baseIndex + 1) % BaseHistory;
                _baseDelays[_baseIndex] = double.PositiveInfinity;
            }

            _baseDelays[_baseIndex] = Math.Min(_baseDelays[_baseIndex], delay);
            sample = delay - _baseDelays.Min();
        }
        else if (_sRtt > 0 && !double.IsPositiveInfinity(_minRtt))
        {
            // No arrival times (a QUIC-like transport): the round trip above its floor.
            sample = _sRtt - _minRtt;
        }

        if (sample is not { } qdelay)
        {
            return;
        }

        _qdelay = Math.Max(0, qdelay);
        _qdelayMin = Math.Min(_qdelayMin, _qdelay);
        if (_options.ReduceJitter)
        {
            _qdelayMaxAvg = Math.Min(_qdelayTarget, Math.Max(_qdelay, _qdelayMaxAvg));
            _qdelayMinAvg = Math.Min(_qdelay, _qdelayMinAvg);
        }

        if (t - _lastQdelayAvgUpdate >= Math.Min(_virtualRtt, SmoothedRtt))
        {
            _qdelayAvg =
                _qdelay < _qdelayAvg
                    ? _qdelay
                    : (QdelayAvgG * _qdelay) + ((1 - QdelayAvgG) * _qdelayAvg);
            if (_options.ReduceJitter)
            {
                _qdelayMaxAvg *= 1 - QdelayMinMaxAvgG;
                _qdelayMinAvg =
                    (_qdelayMinAvg * (1 - QdelayMinMaxAvgG)) + (_qdelayMaxAvg * QdelayMinMaxAvgG);
                _qdelayDevAvg =
                    ((1 - QdelayDevAvgG) * _qdelayDevAvg)
                    + (QdelayDevAvgG * (_qdelayMaxAvg - _qdelayMinAvg));
                _refWndDelayScale = Math.Clamp(1 - (_qdelayDevAvg / QdelayDevThreshold), 0, 1);
            }

            _lastQdelayAvgUpdate = t;
        }
    }

    // Section 4.2.1.3: fast attack, slow decay, at least every 10 ms.
    private void UpdateL4sAlpha(double t)
    {
        if (t - _lastL4sUpdate < Math.Min(0.01, SmoothedRtt) || _unitsDeliveredThisRtt == 0)
        {
            return;
        }

        double fraction = (double)_unitsMarkedThisRtt / _unitsDeliveredThisRtt;
        _l4sAlpha =
            fraction >= _l4sAlpha
                ? (L4sAvgGUp * fraction) + ((1 - L4sAvgGUp) * _l4sAlpha)
                : (1 - L4sAvgGDown) * _l4sAlpha;
        _lastL4sUpdate = t;
        _unitsDeliveredThisRtt = 0;
        _unitsMarkedThisRtt = 0;
    }

    // Sections 4.2.2.1 and 4.2.2.2, with 4.5.2 (conditional loss, policers) and 4.5.3 (undershoot).
    private void UpdateReferenceWindow(double t)
    {
        double scl = Math.Clamp(Math.Pow((ReferenceWindow - _refWndI) / _refWndI * 8, 2), 0.1, 1.0);
        if (_lossDetected || _unitsMarked)
        {
            _lastCongestionDetected = t;
        }

        bool isLoss = false;
        bool isCe = false;
        bool isVirtualCe = false;
        double l4sAlphaV = 0;
        if (_lossDetected)
        {
            // Section 4.5.2: back off only for loss above link-layer noise, or with a queue behind it.
            isLoss = _lossRate > _options.LossRateThreshold || _qdelayAvg > _qdelayTarget / 4;
            if (_lossRate > LossRateThresholdPolicer && _qdelayAvg < _qdelayTarget / 4)
            {
                // Heavy loss with no queue: a rate policer. Cap the window just under where it dropped.
                _maxPolicedRefWnd = ReferenceWindow * BetaLossPolicer;
            }

            if (isLoss)
            {
                _lossEventRate = (0.95 * _lossEventRate) + 0.05;
            }
        }
        else
        {
            _lossEventRate *= 0.95;
        }

        if (!isLoss && _unitsMarked && EcnMode != EcnMode.None)
        {
            isCe = true;
            ClassifyBottleneck();
        }
        else if (!isLoss && !isCe && _qdelayAvg > _qdelayTarget / 2)
        {
            // Pseudo-L4S: queue delay mimics marking from half the target to the target.
            l4sAlphaV = Math.Clamp((_qdelayAvg - (_qdelayTarget / 2)) / (_qdelayTarget / 2), 0, 1);
            isVirtualCe = true;
        }

        if ((isLoss || isCe || isVirtualCe) && _refWndIUpdateAllowed)
        {
            _refWndI = ReferenceWindow;
            _refWndIUpdateAllowed = false;
        }

        // Section 4.5.3: when the target is already well below what got through, a rising round trip has
        // cut the rate; reducing the window as well would undershoot.
        double undershoot =
            _targetBitrate < _ackedBitrate * AckedBitrateMargin ? BackoffScaleLowTargetRate : 1;
        if (isLoss)
        {
            ReferenceWindow *= _options.LossBeta;
        }

        if (isCe)
        {
            if (EcnMode == EcnMode.L4s)
            {
                double backoff = _l4sAlpha / 2 / Math.Max(1, SmoothedRtt / _virtualRtt);
                if (_qdelay < _qdelayTarget * 0.25)
                {
                    backoff *= Math.Max(0.25, scl);
                    backoff *= Math.Max(0.25, _refWndDelayScale);
                }

                if (t - _lastReaction > 100 * Math.Max(_virtualRtt, SmoothedRtt))
                {
                    // Long uncongested and source limited: the window may have grown far above what is
                    // actually in flight, so it starts from there.
                    ReferenceWindow = Math.Min(
                        ReferenceWindow,
                        Math.Max(_maxBytesInFlightPrev, _options.MinReferenceWindow)
                    );
                }

                ReferenceWindow *= 1 - (backoff * undershoot);
            }
            else
            {
                ReferenceWindow *= _options.EcnBeta;
            }
        }

        if (isVirtualCe)
        {
            double backoff = l4sAlphaV / 2 / Math.Max(1, SmoothedRtt / _virtualRtt);
            ReferenceWindow *= 1 - (backoff * undershoot);
        }

        ReferenceWindow = Math.Max(_options.MinReferenceWindow, ReferenceWindow);
        if (isLoss || isCe || isVirtualCe)
        {
            _lastReaction = t;
        }

        _lossDetected = false;
        _unitsMarked = false;
        Increase(t, scl);
        _bytesNewlyAcked = 0;
        _bytesNewlyAckedCe = 0;
    }

    // RFC 9331 section 4.3 item 3: monitor for a classic ECN AQM and answer it classically, which also means
    // marking ECT(0), so the scalable response never meets a classic queue. Switches back only on a path
    // configured for L4S, when marks come at a near-empty queue again.
    private void ClassifyBottleneck()
    {
        if (_options.Ecn != EcnMode.L4s)
        {
            return;
        }

        if (_qdelayAvg >= ClassicMarkingQueueDelay)
        {
            _classicScore = Math.Min(ClassicVerdicts, _classicScore + 1);
        }
        else if (_qdelayAvg <= ScalableMarkingQueueDelay)
        {
            _classicScore = Math.Max(-ClassicVerdicts, _classicScore - 1);
        }

        if (_classicScore >= ClassicVerdicts)
        {
            EcnMode = EcnMode.Classic;
        }
        else if (_classicScore <= -ClassicVerdicts)
        {
            EcnMode = EcnMode.L4s;
        }
    }

    // Section 4.2.2.2.
    private void Increase(double t, double scl)
    {
        double refWndRatio = Math.Min(1, _options.MaxSegmentSize / ReferenceWindow);
        double postCongestion = Math.Clamp(
            (t - _lastCongestionDetected)
                / (_options.PostCongestionDelayRtts * Math.Max(_virtualRtt, SmoothedRtt)),
            0,
            1
        );
        double scaleFactor =
            1 + (_options.MultiplicativeIncreaseFactor * ReferenceWindow / _options.MaxSegmentSize);
        double increment = (_bytesNewlyAcked - _bytesNewlyAckedCe) * refWndRatio;
        increment *= Math.Min(1, SmoothedRtt / _virtualRtt);
        increment *= Math.Max(0.25, scl);
        increment *= Math.Max(0.1, _refWndDelayScale);
        if (scaleFactor > 1)
        {
            scaleFactor = 1 + ((scaleFactor - 1) * postCongestion * scl);
        }

        increment *= scaleFactor;
        double previous = ReferenceWindow;
        double maxAllowed =
            _options.MaxSegmentSize
            + (Math.Max(_maxBytesInFlight, _maxBytesInFlightPrev) * BytesInFlightHeadroom);
        double grown = ReferenceWindow + increment;
        if (grown <= maxAllowed && _targetBitrate < _options.MaxBitrateBps)
        {
            ReferenceWindow = grown;
        }

        ReferenceWindow = Math.Max(
            _options.MinReferenceWindow,
            Math.Min(ReferenceWindow, _maxPolicedRefWnd)
        );
        if (ReferenceWindow > previous)
        {
            _refWndIUpdateAllowed = true;
        }

        // The policer cap lifts by a small fraction a round trip, so a false detection does not stay.
        if (!double.IsPositiveInfinity(_maxPolicedRefWnd))
        {
            _maxPolicedRefWnd *= 1.001;
        }
    }

    // Every 50 ms: the round trip's bytes in flight, the competing-flow target (4.5.4), the clock drift
    // guard (4.5.1), and the window limit near the maximum rate (reference implementation).
    private void SlowUpdate(double t)
    {
        if (t - _rttRolledAt >= SmoothedRtt)
        {
            _maxBytesInFlightPrev = _maxBytesInFlight;
            _maxBytesInFlight = BytesInFlight;
            _rttRolledAt = t;

            // Section 4.5.1: a minimum queue delay that keeps creeping up is clock drift, not queueing.
            if (!double.IsPositiveInfinity(_qdelayMin))
            {
                _delayMinAvg =
                    ((1 - QdelayMinAvgAlpha) * _delayMinAvg) + (QdelayMinAvgAlpha * _qdelayMin);
                _qdelayMin = double.PositiveInfinity;
                if (_delayMinAvg > _qdelayTarget / 4)
                {
                    _delayMinAvg = 0;
                    Array.Fill(_baseDelays, double.PositiveInfinity);
                    _driftHoldUntil = t + Math.Max(5 * SmoothedRtt, 0.2);
                }
            }
        }

        if (t - _lastSlowUpdate < SlowUpdateInterval)
        {
            return;
        }

        _lastSlowUpdate = t;
        if (_options.CompetingFlowCompensation)
        {
            AdjustQueueDelayTarget();
        }

        if (_targetBitrate > _options.MaxBitrateBps * 0.95)
        {
            // Near the maximum the window need not run far ahead of what is in flight; a large one only
            // makes the next congestion episode longer.
            double limit = Math.Max(
                Math.Max(_maxBytesInFlight, _maxBytesInFlightPrev),
                _options.PacingHeadroom * _options.MaxBitrateBps / 8 * (SmoothedRtt + 0.001)
            );
            ReferenceWindow = Math.Max(
                _options.MinReferenceWindow,
                Math.Min(ReferenceWindow, limit)
            );
        }
    }

    // Section 4.5.4.
    private void AdjustQueueDelayTarget()
    {
        _qdelayNorm.Enqueue(_qdelay / _qdelayTargetLo);
        while (_qdelayNorm.Count > QdelayNormHistory)
        {
            _ = _qdelayNorm.Dequeue();
        }

        if (_qdelayNorm.Count < QdelayNormShortHistory)
        {
            return;
        }

        double mean = _qdelayNorm.Average();
        double variance = _qdelayNorm.Sum(v => (v - mean) * (v - mean)) / _qdelayNorm.Count;
        double recentAverage = _qdelayNorm
            .Skip(_qdelayNorm.Count - QdelayNormShortHistory)
            .Average();
        double target = (recentAverage + Math.Sqrt(variance)) * _qdelayTargetLo;
        if (_lossEventRate > 0.002)
        {
            _qdelayTarget = 1.5 * target;
        }
        else if (variance < 0.2)
        {
            _qdelayTarget = target;
        }
        else if (target < _qdelayTargetLo)
        {
            _qdelayTarget = Math.Max(_qdelayTarget * 0.5, target);
        }
        else
        {
            _qdelayTarget *= 0.9;
        }

        _qdelayTarget = Math.Clamp(_qdelayTarget, _qdelayTargetLo, _qdelayTargetHi);
    }

    // Section 4.4.
    private void UpdateTarget(double t)
    {
        double refWndRatio = Math.Min(1, _options.MaxSegmentSize / ReferenceWindow);
        double scale = 1 - Math.Min(0.2, Math.Max(0, refWndRatio - 0.1));
        scale *= (double)_options.MaxSegmentSize / (_options.MaxSegmentSize + PacketOverhead);
        scale /= 1.2 + _rateAdjustFactor + _frameSizeDev;
        double target = scale * 8 * ReferenceWindow / SmoothedRtt;
        if (t < _driftHoldUntil)
        {
            // Section 4.5.1: half rate for a few round trips while the base delay is measured afresh.
            target *= 0.5;
        }

        _targetBitrate = Math.Clamp(target, _options.MinBitrateBps, _options.MaxBitrateBps);
        Current = Estimate();
    }

    // Section 4.3.2: pacing above the target, relaxed as the target nears the maximum.
    private CapacityEstimate Estimate()
    {
        double pace = Math.Max(RatePaceMin, _targetBitrate) * _options.PacingHeadroom;
        double nominal = _targetBitrate / _options.MaxBitrateBps;
        double relax = Math.Min(1, (nominal - RelaxedPacingLimitLow) / (1 - RelaxedPacingLimitLow));
        relax = Math.Min(1, Math.Max(1 / _options.MaxRelaxedPacingFactor, 1 - relax));
        pace /= relax;
        return new CapacityEstimate(
            (long)_targetBitrate,
            (long)pace,
            TimeSpan.FromSeconds(_sRtt),
            double.IsPositiveInfinity(_minRtt) ? TimeSpan.Zero : TimeSpan.FromSeconds(_minRtt)
        );
    }
}
