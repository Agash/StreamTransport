namespace Agash.StreamTransport.Adaptation;

/// <summary>
/// A transport congestion controller: it turns what was sent and what feedback said about it into the
/// capacity of the path. A transport that runs its own congestion control (QUIC, SRT) reports that
/// controller's capacity instead of using one of these.
/// </summary>
public interface ICongestionController
{
    /// <summary>The latest estimate.</summary>
    CapacityEstimate Current { get; }

    /// <summary>Notes a packet as it goes on the wire.</summary>
    /// <param name="packet">The packet.</param>
    void OnPacketSent(in SentPacket packet);

    /// <summary>
    /// Whether a packet may go now: the send window, which keeps the bytes in flight near what the path
    /// carries. Media other than audio waits while it is closed.
    /// </summary>
    /// <param name="size">The packet's size on the wire.</param>
    /// <param name="now">Now, on the sender's monotonic clock.</param>
    /// <returns>True when it may go.</returns>
    bool CanTransmit(int size, TimeSpan now);

    /// <summary>
    /// Notes an encoded video frame as it is queued to send, with how long the oldest queued packet has
    /// waited, so the target leaves room for frames larger than nominal and keeps the sender queue short.
    /// </summary>
    /// <param name="bytes">The frame's size.</param>
    /// <param name="queueDelay">How long the oldest packet waiting to be sent has waited.</param>
    /// <param name="now">Now, on the sender's monotonic clock.</param>
    void OnMediaFrame(int bytes, TimeSpan queueDelay, TimeSpan now);

    /// <summary>Folds in the outcomes one piece of feedback resolved.</summary>
    /// <param name="observations">The packets the feedback resolved.</param>
    /// <param name="roundTrip">A round-trip sample the feedback gave, if any.</param>
    /// <param name="now">Now, on the sender's monotonic clock.</param>
    /// <returns>The updated estimate.</returns>
    CapacityEstimate OnFeedback(
        ReadOnlySpan<PacketObservation> observations,
        TimeSpan? roundTrip,
        TimeSpan now
    );

    /// <summary>Lets the controller act on the passage of time, every few tens of milliseconds.</summary>
    /// <param name="now">Now, on the sender's monotonic clock.</param>
    /// <returns>The updated estimate.</returns>
    CapacityEstimate OnTick(TimeSpan now);
}
