namespace Agash.StreamTransport.WebRtc;

/// <summary>A record of a packet the sender put on the wire, fed to the congestion controller.</summary>
/// <param name="SequenceNumber">The RTP sequence number.</param>
/// <param name="SizeBytes">The packet size in bytes (including headers).</param>
/// <param name="SendTimeMicros">The send time, in microseconds on a monotonic clock.</param>
public readonly record struct SentPacketInfo(ushort SequenceNumber, int SizeBytes, long SendTimeMicros);

/// <summary>
/// The per-packet outcome the sender derives by correlating its sent packets with RFC 8888 feedback:
/// whether the packet arrived and, if so, when.
/// </summary>
/// <param name="SequenceNumber">The RTP sequence number.</param>
/// <param name="SizeBytes">The packet size in bytes.</param>
/// <param name="SendTimeMicros">When the sender sent it (monotonic µs).</param>
/// <param name="ReceiveTimeMicros">When the receiver got it (monotonic µs in the sender's frame), or -1 if lost.</param>
public readonly record struct PacketResult(ushort SequenceNumber, int SizeBytes, long SendTimeMicros, long ReceiveTimeMicros)
{
    /// <summary>Whether the packet was received.</summary>
    public bool Received => ReceiveTimeMicros >= 0;
}

/// <summary>
/// The controller's output: the bitrate the encoder should target, the rate the pacer should drain at, and
/// the controller's RTT estimate (libwebrtc surfaces the same as <c>network_estimate.round_trip_time</c>).
/// </summary>
/// <param name="TargetBitrateBps">The encoder target bitrate in bits per second.</param>
/// <param name="PacingRateBps">The pacer drain rate in bits per second (typically above the target for headroom).</param>
/// <param name="SmoothedRttMicros">The smoothed RTT (EWMA, α=1/8) in microseconds, or 0 before any feedback.</param>
/// <param name="BaseRttMicros">The minimum observed RTT in microseconds (the propagation floor), or 0.</param>
public readonly record struct BitrateEstimate(
    long TargetBitrateBps, long PacingRateBps, long SmoothedRttMicros = 0, long BaseRttMicros = 0);

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
    double LossRate, long SmoothedRttMicros, long BaseRttMicros, long TargetBitrateBps, long PacingRateBps)
{
    /// <summary>The queue-delay (bufferbloat) estimate: smoothed RTT above the propagation floor, in microseconds.</summary>
    public long QueueDelayMicros => BaseRttMicros > 0 && SmoothedRttMicros > BaseRttMicros ? SmoothedRttMicros - BaseRttMicros : 0;
}

/// <summary>
/// A send-side congestion controller (modelled on libwebrtc's <c>NetworkControllerInterface</c>): it is fed
/// sent packets and per-packet feedback, and produces a target bitrate + pacing rate. Implementations are
/// pluggable (Google Congestion Control, SCReAM for cellular) so the transport core never depends on a
/// particular algorithm.
/// </summary>
public interface INetworkController
{
    /// <summary>The latest estimate.</summary>
    BitrateEstimate CurrentEstimate { get; }

    /// <summary>Records a packet the sender transmitted.</summary>
    void OnPacketSent(in SentPacketInfo packet);

    /// <summary>Feeds a batch of per-packet feedback (from RFC 8888) and returns the updated estimate.</summary>
    BitrateEstimate OnFeedback(ReadOnlySpan<PacketResult> results, long nowMicros);

    /// <summary>Called periodically (e.g. every 25 ms) to let the controller adapt without new feedback.</summary>
    BitrateEstimate OnProcessInterval(long nowMicros);
}
