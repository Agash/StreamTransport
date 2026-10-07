namespace Agash.StreamTransport.Adaptation;

/// <summary>A packet a transport put on the wire.</summary>
/// <param name="Id">
/// The transport's identity for it, unique among the packets in flight and increasing in send order
/// (an RTP adapter numbers its sends; a QUIC adapter can use packet numbers).
/// </param>
/// <param name="Size">Its size on the wire, in bytes.</param>
/// <param name="SentAt">When it was sent, on the sender's monotonic clock.</param>
/// <param name="Class">What it carries.</param>
public readonly record struct SentPacket(long Id, int Size, TimeSpan SentAt, TrafficClass Class);

/// <summary>What one piece of feedback says about a sent packet.</summary>
/// <param name="Id">The packet's <see cref="SentPacket.Id"/>.</param>
/// <param name="Received">Whether the receiver had it when it reported.</param>
/// <param name="ArrivedAt">
/// When it arrived, on the receiver's clock, when the feedback says (RFC 8888 does per packet; QUIC
/// acknowledgements do not). Only differences between arrivals are meaningful across the two clocks.
/// </param>
/// <param name="Ecn">The ECN codepoint it arrived with.</param>
public readonly record struct PacketReport(
    long Id,
    bool Received,
    TimeSpan? ArrivedAt,
    EcnCodepoint Ecn
);

/// <summary>What became of a sent packet, as far as the sender can tell.</summary>
public enum PacketOutcome
{
    /// <summary>It arrived.</summary>
    Delivered,

    /// <summary>It was missing for longer than the reordering window: a loss event.</summary>
    Lost,

    /// <summary>
    /// It arrived after it was declared lost: it was reordered, not lost, and the reordering window grows to
    /// cover such delays.
    /// </summary>
    DeliveredLate,
}

/// <summary>A sent packet's outcome, resolved from feedback.</summary>
/// <param name="Packet">The packet.</param>
/// <param name="Outcome">What became of it.</param>
/// <param name="ArrivedAt">When it arrived on the receiver's clock, when known.</param>
/// <param name="Ecn">The ECN codepoint it arrived with.</param>
public readonly record struct PacketObservation(
    SentPacket Packet,
    PacketOutcome Outcome,
    TimeSpan? ArrivedAt,
    EcnCodepoint Ecn
);
