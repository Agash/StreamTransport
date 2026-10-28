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

    /// <summary>
    /// The ECT codepoint the controller's congestion response suits: ECT(1) for a scalable (L4S) response,
    /// ECT(0) for a classic one (RFC 9331 section 4.3: a classic response must not tag ECT(1)), not-ECT
    /// when it does not use ECN. The transport marks with it once the path is validated.
    /// </summary>
    EcnCodepoint Ecn { get; }

    /// <summary>
    /// Tells the controller which codepoint the path's packets carry: not-ECT before ECN is agreed and
    /// validated or after it fails, ECT(0) when the peer did not ask for ECT(1). It answers CE marks to
    /// suit: classic for ECT(0), scalable for ECT(1).
    /// </summary>
    /// <param name="codepoint">The codepoint.</param>
    void UseEcn(EcnCodepoint codepoint);

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

    /// <summary>
    /// Starts over on a new network path (RFC 9000 section 9.4): the capacity of the new path may be
    /// nothing like the old one's, so every estimate goes back to its initial value. The transport does not
    /// call this when only a port changed, as NAT rebinding does, since the path is the same.
    /// </summary>
    /// <param name="roundTrip">The new path's round trip, measured before the switch, if known.</param>
    /// <param name="now">Now, on the sender's monotonic clock.</param>
    void OnPathChanged(TimeSpan? roundTrip, TimeSpan now);

    /// <summary>Lets the controller act on the passage of time, every few tens of milliseconds.</summary>
    /// <param name="now">Now, on the sender's monotonic clock.</param>
    /// <returns>The updated estimate.</returns>
    CapacityEstimate OnTick(TimeSpan now);
}
