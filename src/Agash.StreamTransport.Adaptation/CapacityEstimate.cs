namespace Agash.StreamTransport.Adaptation;

/// <summary>
/// What a path can carry now: the rate to send media at, the rate to pace packets at, and the round trip
/// behind both.
/// </summary>
/// <param name="TargetBitsPerSecond">The rate all traffic together may use, which the media-rate allocator splits.</param>
/// <param name="PacingBitsPerSecond">The rate the pacer drains at, above the target to absorb frame bursts.</param>
/// <param name="SmoothedRoundTrip">The smoothed round trip, or zero before one was measured.</param>
/// <param name="MinimumRoundTrip">The least round trip seen, the propagation floor, or zero.</param>
public readonly record struct CapacityEstimate(
    long TargetBitsPerSecond,
    long PacingBitsPerSecond,
    TimeSpan SmoothedRoundTrip = default,
    TimeSpan MinimumRoundTrip = default
)
{
    /// <summary>The queue the round trip shows above its floor.</summary>
    public TimeSpan QueueDelay =>
        MinimumRoundTrip > TimeSpan.Zero && SmoothedRoundTrip > MinimumRoundTrip
            ? SmoothedRoundTrip - MinimumRoundTrip
            : TimeSpan.Zero;
}
