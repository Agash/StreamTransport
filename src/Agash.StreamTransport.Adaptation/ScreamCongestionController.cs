namespace Agash.StreamTransport.Adaptation;

/// <summary>
/// A SCReAM-style send-side congestion controller (RFC 8298): a congestion window in bytes is grown while
/// the queuing delay stays under target and shrunk multiplicatively on loss or excess delay; the target
/// bitrate is the window divided by the smoothed round-trip time. Tuned for cellular links - it reacts to
/// standing queue build-up (bufferbloat) before loss, which is the dominant failure mode on mobile uplinks.
/// </summary>
/// <remarks>
/// Queue delay is estimated as <c>sRTT − baseRTT</c> from the round-trip samples the
/// <see cref="DeliveryTracker"/> takes, which exclude the receiver's hold time. Loss reaches the controller
/// only once the tracker's reordering window has passed, and a packet that arrives after all is reported
/// as recovered, so reordering is not congestion. On a feedback stall - a likely radio outage - the window
/// decays so the encoder does not blast a recovering link.
/// </remarks>
public sealed class ScreamCongestionController : ICongestionController
{
    private readonly ScreamOptions _options;
    private readonly double _minCwnd;
    private readonly LossEstimator _lossEstimator;

    private double _cwndBytes;
    private double _srttMicros;
    private double _baseRttMicros = double.MaxValue;
    private double _l4sAlpha; // smoothed L4S/ECN-CE marking fraction (RFC 9331 / DCTCP alpha).
    private long _lastFeedbackMicros;
    private long _targetBitrate;

    /// <summary>Creates the controller with the given (or default) tunables.</summary>
    public ScreamCongestionController(ScreamOptions? options = null)
    {
        _options = options ?? new ScreamOptions();
        _lossEstimator = new LossEstimator(
            _options.VirtualRttMs * 1000L,
            _options.RttsWithLossBeforeBackoff,
            _options.LosslessRttsBeforeClear
        );
        _minCwnd = (_options.MinBitrateBps / 8.0) * 0.05; // ~50 ms at the floor rate
        _cwndBytes = Math.Max(_minCwnd, (_options.StartBitrateBps / 8.0) * 0.1);
        _targetBitrate = _options.StartBitrateBps;
        Current = BuildEstimate();
    }

    /// <inheritdoc/>
    public CapacityEstimate Current { get; private set; }

    /// <inheritdoc/>
    public void OnPacketSent(in SentPacket packet)
    {
        // This window/RTT-rate model derives everything from feedback; nothing to track on send.
    }

    /// <inheritdoc/>
    public CapacityEstimate OnFeedback(
        ReadOnlySpan<PacketObservation> observations,
        TimeSpan? roundTrip,
        TimeSpan now
    )
    {
        long nowMicros = Micros(now);
        if (observations.Length == 0 && roundTrip is null)
        {
            return Current;
        }

        _lastFeedbackMicros = nowMicros;

        int lostCount = 0;
        int recoveredCount = 0;
        long ackedBytes = 0;
        int receivedCount = 0;
        int ceCount = 0;
        foreach (PacketObservation observation in observations)
        {
            if (observation.Outcome == PacketOutcome.Lost)
            {
                lostCount++;
                continue;
            }

            if (observation.Outcome == PacketOutcome.DeliveredLate)
            {
                recoveredCount++;
            }

            receivedCount++;
            if (observation.Ecn == EcnCodepoint.Ce)
            {
                ceCount++;
            }

            ackedBytes += observation.Packet.Size;
        }

        if (roundTrip is { } rtt)
        {
            double rttSample = Math.Max(1, Micros(rtt));
            _srttMicros =
                _srttMicros == 0 ? rttSample : (_srttMicros * 0.875) + (rttSample * 0.125);
            _baseRttMicros = Math.Min(_baseRttMicros, rttSample);
        }

        double queueDelayMicros = _srttMicros - _baseRttMicros;
        double targetMicros = _options.QueueDelayTargetMs * 1000.0;

        // Run loss through the SCReAM v2 asymmetric filter rather than reacting to each lost packet. The filter
        // only reports congestion after sustained loss, so spurious wireless loss holds the rate steady
        // instead of collapsing it. A packet declared lost that arrived after all was reordered, not lost.
        _lossEstimator.Update(lostCount, recoveredCount, nowMicros, _srttMicros);
        bool delayCongested = queueDelayMicros > targetMicros;

        // Update the L4S marking fraction estimate every feedback that carried receptions, with a fast-attack /
        // slow-decay EWMA (SCReAM v2 §4.2.1.3, RFC 9331 / DCTCP): it rises quickly when marks appear and decays
        // slowly when they stop, so the ECN back-off tracks sustained congestion rather than per-feedback noise.
        if (receivedCount > 0)
        {
            double fractionMarked = (double)ceCount / receivedCount;
            _l4sAlpha =
                fractionMarked > _l4sAlpha
                    ? Math.Min(
                        1.0,
                        (_options.L4sAlphaGainUp * fractionMarked)
                            + ((1.0 - _options.L4sAlphaGainUp) * _l4sAlpha)
                    )
                    : (1.0 - _options.L4sAlphaGainDown) * _l4sAlpha;
        }

        if (delayCongested || _lossEstimator.Congested)
        {
            _cwndBytes = Math.Max(_minCwnd, _cwndBytes * _options.BackoffFactor);
        }
        else if (ceCount > 0)
        {
            // L4S/ECN-CE: the network marked congestion before any loss or standing queue. Back off by half the
            // smoothed marking fraction (DCTCP's cwnd *= 1 - alpha/2, RFC 9331 / SCReAM v2 UpdateRefWindow). At
            // saturating marks this approaches a 50% cut; at light marking it barely dips - always gentler than
            // the loss back-off, letting the controller hold a higher, smoother rate on an L4S-capable bottleneck.
            _cwndBytes = Math.Max(_minCwnd, _cwndBytes * (1.0 - (_l4sAlpha / 2.0)));
        }
        else if (ackedBytes > 0 && !_lossEstimator.IncreaseBlocked)
        {
            // Grow only when no congestion memory remains (SCReAM v2 blocks the increase while the loss filter is
            // non-zero, so the window does not re-expand the instant an episode ends). Cap growth at the
            // bandwidth-delay product at the ceiling rate, so the window cannot wind up past what the max rate
            // needs - otherwise a later multiplicative back-off is hidden by the rate clamp.
            double offTarget = (targetMicros - queueDelayMicros) / targetMicros; // (0, 1]
            _cwndBytes = Math.Min(MaxCwnd(), _cwndBytes + (offTarget * ackedBytes));
        }

        UpdateTarget();
        return Current;
    }

    /// <inheritdoc/>
    public CapacityEstimate OnTick(TimeSpan now)
    {
        long nowMicros = Micros(now);
        // No feedback for a second → assume the link is in trouble and ease off the window.
        if (_lastFeedbackMicros != 0 && nowMicros - _lastFeedbackMicros > 1_000_000)
        {
            _cwndBytes = Math.Max(_minCwnd, _cwndBytes * 0.95);
            UpdateTarget();
        }

        return Current;
    }

    private double SrttSeconds() => (_srttMicros > 0 ? _srttMicros : 50_000) / 1_000_000.0;

    private double MaxCwnd() => Math.Max(_minCwnd, (_options.MaxBitrateBps / 8.0) * SrttSeconds());

    private void UpdateTarget()
    {
        double srttSeconds = SrttSeconds();
        _cwndBytes = Math.Clamp(_cwndBytes, _minCwnd, MaxCwnd()); // anti-windup
        long rate = (long)(_cwndBytes * 8 / srttSeconds);
        _targetBitrate = Math.Clamp(rate, _options.MinBitrateBps, _options.MaxBitrateBps);
        Current = BuildEstimate();
    }

    private CapacityEstimate BuildEstimate() =>
        new(
            _targetBitrate,
            (long)(_targetBitrate * _options.PacingHeadroom),
            TimeSpan.FromMicroseconds((long)_srttMicros),
            _baseRttMicros is double.MaxValue
                ? TimeSpan.Zero
                : TimeSpan.FromMicroseconds((long)_baseRttMicros)
        );

    private static long Micros(TimeSpan time) => time.Ticks / TimeSpan.TicksPerMicrosecond;
}
