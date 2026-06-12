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

    /// <summary>The pacing headroom over the target rate (RFC 8298 pacing uses ~1.25–2.5×).</summary>
    public double PacingHeadroom { get; set; } = 1.25;

    /// <summary>The maximum segment size assumed for the congestion window (bytes).</summary>
    public int MaxSegmentSize { get; set; } = 1200;
}
