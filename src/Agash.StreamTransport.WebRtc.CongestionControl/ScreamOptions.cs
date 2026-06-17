namespace Agash.StreamTransport.WebRtc.CongestionControl;

/// <summary>Tunables for <see cref="ScreamCongestionController"/> (RFC 8298 parameters).</summary>
public sealed class ScreamOptions
{
    /// <summary>The minimum target bitrate (bits/s). The controller never drops below this.</summary>
    public long MinBitrateBps { get; set; } = 150_000;

    /// <summary>The maximum target bitrate (bits/s) the controller will ramp to.</summary>
    public long MaxBitrateBps { get; set; } = 10_000_000;

    /// <summary>The initial target bitrate (bits/s).</summary>
    public long StartBitrateBps { get; set; } = 600_000;

    /// <summary>The queue-delay target in milliseconds (RFC 8298 QDELAY_TARGET_LO); growth stops above it.</summary>
    public int QueueDelayTargetMs { get; set; } = 60;

    /// <summary>The multiplicative back-off applied on loss or excess queue delay (RFC 8298 beta).</summary>
    public double BackoffFactor { get; set; } = 0.8;

    /// <summary>The pacing headroom over the target rate (RFC 8298 pacing uses ~1.25-2.5×).</summary>
    public double PacingHeadroom { get; set; } = 1.25;

    /// <summary>
    /// Fast-attack EWMA gain for the L4S/ECN-CE marking fraction (SCReAM v2 L4sAvgGUp, RFC 9331 / DCTCP alpha).
    /// Applied when the current marked fraction exceeds the smoothed estimate, so the controller ramps its ECN
    /// reaction up quickly.
    /// </summary>
    public double L4sAlphaGainUp { get; set; } = 1.0 / 8.0;

    /// <summary>
    /// Slow-decay EWMA gain for the L4S/ECN-CE marking fraction (SCReAM v2 L4sAvgGDown). Applied when marking
    /// eases, so the smoothed estimate (and thus the back-off) relaxes gradually rather than snapping to zero.
    /// </summary>
    public double L4sAlphaGainDown { get; set; } = 1.0 / 128.0;

    /// <summary>The maximum segment size assumed for the congestion window (bytes).</summary>
    public int MaxSegmentSize { get; set; } = 1200;

    /// <summary>
    /// The nominal RTT (ms) the loss estimator clamps to when the measured RTT is smaller (SCReAM v2 VirtualRtt),
    /// so the per-RTT loss filter steps at a stable cadence on very low-RTT LAN links.
    /// </summary>
    public int VirtualRttMs { get; set; } = 25;

    /// <summary>
    /// Consecutive loss-bearing RTTs required before a loss-driven back-off fires (SCReAM v2
    /// RttsWithLossBeforeBackoff). The loss filter steps up by 1/this each such RTT; the default of 3 ignores
    /// spurious wireless loss while still reacting to sustained congestion within a few RTTs.
    /// </summary>
    public int RttsWithLossBeforeBackoff { get; set; } = 3;

    /// <summary>
    /// Lossless RTTs to fully clear the congestion level (SCReAM v2 LosslessRttsBeforeClear). The filter steps
    /// down by 1/this each lossless RTT; the default of 2 keeps brief memory of a real congestion episode.
    /// </summary>
    public int LosslessRttsBeforeClear { get; set; } = 2;
}
