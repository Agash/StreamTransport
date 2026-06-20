namespace Agash.StreamTransport.WebRtc.CongestionControl;

/// <summary>
/// SCReAM v2 loss estimator (a port of libwebrtc's <c>modules/congestion_controller/scream/loss_estimator</c>):
/// a biased asymmetric step filter that converts per-feedback loss counts into a normalized short-term
/// congestion level in [0, 1]. It steps <i>up</i> by <c>1/rttsWithLossBeforeBackoff</c> for each RTT that saw
/// any loss and <i>down</i> by <c>1/losslessRttsBeforeClear</c> for each lossless RTT.
/// </summary>
/// <remarks>
/// The asymmetry is the whole point: on a wireless link with ~1% uniform random loss roughly 40% of RTTs see a
/// loss, yet the expected per-RTT drift is still negative (0.4·(+1/3) + 0.6·(−1/2) ≈ −0.17), so spurious loss
/// never reaches the back-off threshold. Only sustained loss (≈3 consecutive RTTs) drives <see cref="Congested"/>
/// true. This stops the controller from collapsing the rate on a flaky-but-not-congested radio - exactly the
/// failure mode a naive "back off on any lost packet" controller suffers.
/// </remarks>
internal sealed class LossEstimator(long virtualRttMicros, int rttsWithLossBeforeBackoff, int losslessRttsBeforeClear)
{
    private double _congestionLevel;
    private bool _lossEventThisRtt;
    private int _unrecoveredLostPackets;
    private long _lastLossOrRecoveryMicros;
    private long _lastRttUpdateMicros;

    /// <summary>True once sustained loss has driven the congestion level to the back-off threshold.</summary>
    public bool Congested => _congestionLevel >= 0.99;

    /// <summary>
    /// True while any congestion memory remains: window growth is blocked here even on a loss-free report, so the
    /// window does not re-expand the instant a congestion episode ends (it takes the full decay back to zero).
    /// </summary>
    public bool IncreaseBlocked => _congestionLevel >= 0.01;

    /// <summary>
    /// Fold one feedback report's loss/recovery counts into the estimate. <paramref name="numRecovered"/> is the
    /// count of previously-lost packets that arrived late (e.g. via NACK/RTX); recovering all outstanding loss
    /// clears the level immediately, since recovered loss was never congestion.
    /// </summary>
    public void Update(int numLost, int numRecovered, long feedbackMicros, double rttMicros)
    {
        long maxRtt = Math.Max(virtualRttMicros, (long)rttMicros);

        // Drop the unrecovered count if a whole RTT passed with no loss or recovery, so packets that are
        // permanently lost (never NACK-recovered) cannot pin the counter positive forever.
        if (feedbackMicros - _lastLossOrRecoveryMicros > maxRtt)
        {
            _unrecoveredLostPackets = 0;
        }

        if (numLost > 0 || numRecovered > 0)
        {
            _lastLossOrRecoveryMicros = feedbackMicros;
        }

        if (numRecovered > 0)
        {
            _unrecoveredLostPackets = Math.Max(0, _unrecoveredLostPackets - numRecovered);
            if (_unrecoveredLostPackets == 0)
            {
                _congestionLevel = 0.0;
                _lossEventThisRtt = false;
            }
        }

        if (numLost > 0)
        {
            _unrecoveredLostPackets += numLost;
            _lossEventThisRtt = true;
        }

        // Step the filter at most once per RTT: up if this RTT saw loss, down if it was lossless.
        if (feedbackMicros - _lastRttUpdateMicros >= maxRtt)
        {
            _lastRttUpdateMicros = feedbackMicros;
            _congestionLevel = _lossEventThisRtt
                ? Math.Min(1.0, _congestionLevel + (1.0 / rttsWithLossBeforeBackoff))
                : Math.Max(0.0, _congestionLevel - (1.0 / losslessRttsBeforeClear));
            _lossEventThisRtt = false;
        }
    }
}
