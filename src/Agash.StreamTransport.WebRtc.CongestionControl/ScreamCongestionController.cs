namespace Agash.StreamTransport.WebRtc.CongestionControl;

/// <summary>
/// A SCReAM-style send-side congestion controller (RFC 8298): a congestion window in bytes is grown while
/// the queuing delay stays under target and shrunk multiplicatively on loss or excess delay; the target
/// bitrate is the window divided by the smoothed round-trip time. Tuned for cellular links - it reacts to
/// standing queue build-up (bufferbloat) before loss, which is the dominant failure mode on mobile uplinks.
/// </summary>
/// <remarks>
/// Queue delay is estimated as <c>sRTT − baseRTT</c> (RTT-based, no clock-sync needed). On a feedback
/// stall - a likely radio outage - the window decays so the encoder does not blast a recovering link.
/// </remarks>
public sealed class ScreamCongestionController : INetworkController
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
            _options.VirtualRttMs * 1000L, _options.RttsWithLossBeforeBackoff, _options.LosslessRttsBeforeClear);
        _minCwnd = (_options.MinBitrateBps / 8.0) * 0.05;   // ~50 ms at the floor rate
        _cwndBytes = Math.Max(_minCwnd, (_options.StartBitrateBps / 8.0) * 0.1);
        _targetBitrate = _options.StartBitrateBps;
        CurrentEstimate = BuildEstimate();
    }

    /// <inheritdoc/>
    public BitrateEstimate CurrentEstimate { get; private set; }

    /// <inheritdoc/>
    public void OnPacketSent(in SentPacketInfo packet)
    {
        // This window/RTT-rate model derives everything from feedback; nothing to track on send.
    }

    /// <inheritdoc/>
    public BitrateEstimate OnFeedback(ReadOnlySpan<PacketResult> results, long nowMicros)
    {
        if (results.Length == 0)
        {
            return CurrentEstimate;
        }

        _lastFeedbackMicros = nowMicros;

        int lostCount = 0;
        long ackedBytes = 0;
        long latestSendMicros = long.MinValue;
        bool anyReceived = false;
        int receivedCount = 0;
        int ceCount = 0;
        foreach (PacketResult result in results)
        {
            if (!result.Received)
            {
                lostCount++;
                continue;
            }

            anyReceived = true;
            receivedCount++;
            if (result.CongestionExperienced)
            {
                ceCount++;
            }

            ackedBytes += result.SizeBytes;
            latestSendMicros = Math.Max(latestSendMicros, result.SendTimeMicros);
        }

        if (anyReceived)
        {
            double rttSample = Math.Max(1, nowMicros - latestSendMicros);
            _srttMicros = _srttMicros == 0 ? rttSample : (_srttMicros * 0.875) + (rttSample * 0.125);
            _baseRttMicros = Math.Min(_baseRttMicros, rttSample);
        }

        double queueDelayMicros = _srttMicros - _baseRttMicros;
        double targetMicros = _options.QueueDelayTargetMs * 1000.0;

        // Run loss through the SCReAM v2 asymmetric filter rather than reacting to each lost packet. The filter
        // only reports congestion after sustained loss, so spurious wireless loss (now also masked by NACK/RTX
        // recovery) holds the rate steady instead of collapsing it. Recovered-packet accounting is a follow-up;
        // for now lost packets that NACK/RTX recovers simply stop arriving as "not received" in later reports.
        _lossEstimator.Update(lostCount, numRecovered: 0, nowMicros, _srttMicros);
        bool delayCongested = queueDelayMicros > targetMicros;

        // Update the L4S marking fraction estimate every feedback that carried receptions, with a fast-attack /
        // slow-decay EWMA (SCReAM v2 §4.2.1.3, RFC 9331 / DCTCP): it rises quickly when marks appear and decays
        // slowly when they stop, so the ECN back-off tracks sustained congestion rather than per-feedback noise.
        if (receivedCount > 0)
        {
            double fractionMarked = (double)ceCount / receivedCount;
            _l4sAlpha = fractionMarked > _l4sAlpha
                ? Math.Min(1.0, (_options.L4sAlphaGainUp * fractionMarked) + ((1.0 - _options.L4sAlphaGainUp) * _l4sAlpha))
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
        return CurrentEstimate;
    }

    /// <inheritdoc/>
    public BitrateEstimate OnProcessInterval(long nowMicros)
    {
        // No feedback for a second → assume the link is in trouble and ease off the window.
        if (_lastFeedbackMicros != 0 && nowMicros - _lastFeedbackMicros > 1_000_000)
        {
            _cwndBytes = Math.Max(_minCwnd, _cwndBytes * 0.95);
            UpdateTarget();
        }

        return CurrentEstimate;
    }

    private double SrttSeconds() => (_srttMicros > 0 ? _srttMicros : 50_000) / 1_000_000.0;

    private double MaxCwnd() => Math.Max(_minCwnd, (_options.MaxBitrateBps / 8.0) * SrttSeconds());

    private void UpdateTarget()
    {
        double srttSeconds = SrttSeconds();
        _cwndBytes = Math.Clamp(_cwndBytes, _minCwnd, MaxCwnd()); // anti-windup
        long rate = (long)(_cwndBytes * 8 / srttSeconds);
        _targetBitrate = Math.Clamp(rate, _options.MinBitrateBps, _options.MaxBitrateBps);
        CurrentEstimate = BuildEstimate();
    }

    private BitrateEstimate BuildEstimate() => new(
        _targetBitrate,
        (long)(_targetBitrate * _options.PacingHeadroom),
        (long)_srttMicros,
        _baseRttMicros is double.MaxValue ? 0 : (long)_baseRttMicros);
}
