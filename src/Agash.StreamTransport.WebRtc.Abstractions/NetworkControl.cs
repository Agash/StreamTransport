namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// A snapshot of transport link health: loss, RTT, and the controller's
/// rate estimate, aggregated so the mobility layer / a host UI can reason about one signal. Jitter and pacer
/// backlog are additional inputs that can join this as they are surfaced.
/// </summary>
/// <param name="LossRate">Fraction of packets reported lost over the recent window (0..1, EWMA).</param>
/// <param name="SmoothedRttMicros">Smoothed RTT in microseconds.</param>
/// <param name="BaseRttMicros">Minimum observed RTT in microseconds (propagation floor).</param>
/// <param name="TargetBitrateBps">The current congestion-controlled target bitrate.</param>
/// <param name="PacingRateBps">The current pacing rate.</param>
public readonly record struct TransportHealthMetrics(
    double LossRate,
    long SmoothedRttMicros,
    long BaseRttMicros,
    long TargetBitrateBps,
    long PacingRateBps
)
{
    /// <summary>The queue-delay (bufferbloat) estimate: smoothed RTT above the propagation floor, in microseconds.</summary>
    public long QueueDelayMicros =>
        BaseRttMicros > 0 && SmoothedRttMicros > BaseRttMicros
            ? SmoothedRttMicros - BaseRttMicros
            : 0;
}

/// <summary>
/// Lifetime loss-recovery counters for one peer connection, for pipeline debug telemetry. The media layer
/// snapshots these and logs per-second deltas so loss can be localised: how many primary packets were sent,
/// how many retransmissions (RTX) the sender served, how many sequences a receiver NACK'd, how many packets
/// RTX actually recovered on the receiver, and how many keyframe (PLI) requests were sent.
/// </summary>
/// <param name="MediaPacketsSent">Primary RTP packets transmitted (all media).</param>
/// <param name="RtxPacketsSent">RTX retransmission packets transmitted in response to NACKs.</param>
/// <param name="NackSequencesRequested">Sequence numbers requested across all NACKs sent.</param>
/// <param name="RtxPacketsRecovered">Inbound RTX packets successfully unwrapped to their original packet.</param>
/// <param name="KeyframeRequestsSent">Keyframe (PLI) requests sent to the peer.</param>
public readonly record struct TransportLossStats(
    long MediaPacketsSent,
    long RtxPacketsSent,
    long NackSequencesRequested,
    long RtxPacketsRecovered,
    long KeyframeRequestsSent
);
