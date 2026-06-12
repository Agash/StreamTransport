using System.Buffers.Binary;

namespace Agash.StreamTransport.WebRtc.Rtcp;

/// <summary>RTCP packet types (RFC 3550 §6 and the AVPF/feedback extensions).</summary>
public enum RtcpPacketType : byte
{
    /// <summary>Sender Report (RFC 3550 §6.4.1).</summary>
    SenderReport = 200,

    /// <summary>Receiver Report (RFC 3550 §6.4.2).</summary>
    ReceiverReport = 201,

    /// <summary>Source Description.</summary>
    SourceDescription = 202,

    /// <summary>Goodbye.</summary>
    Goodbye = 203,

    /// <summary>Transport-layer feedback (RFC 4585) — NACK, and RFC 8888 congestion control feedback.</summary>
    TransportFeedback = 205,

    /// <summary>Payload-specific feedback (RFC 4585) — PLI, FIR.</summary>
    PayloadFeedback = 206,
}

/// <summary>
/// One RTCP reception report block (RFC 3550 §6.4.1): the receiver's view of a source — loss, the extended
/// highest sequence number received, jitter, and the timestamps used to compute round-trip time.
/// </summary>
public readonly record struct RtcpReportBlock(
    uint Ssrc,
    byte FractionLost,
    int CumulativeLost,
    uint ExtendedHighestSequence,
    uint InterarrivalJitter,
    uint LastSenderReport,
    uint DelaySinceLastSenderReport)
{
    internal const int Length = 24;

    internal void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination, Ssrc);
        destination[4] = FractionLost;
        destination[5] = (byte)(CumulativeLost >> 16);
        destination[6] = (byte)(CumulativeLost >> 8);
        destination[7] = (byte)CumulativeLost;
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], ExtendedHighestSequence);
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..], InterarrivalJitter);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], LastSenderReport);
        BinaryPrimitives.WriteUInt32BigEndian(destination[20..], DelaySinceLastSenderReport);
    }

    internal static RtcpReportBlock Read(ReadOnlySpan<byte> source) => new(
        BinaryPrimitives.ReadUInt32BigEndian(source),
        source[4],
        (source[5] << 16) | (source[6] << 8) | source[7],
        BinaryPrimitives.ReadUInt32BigEndian(source[8..]),
        BinaryPrimitives.ReadUInt32BigEndian(source[12..]),
        BinaryPrimitives.ReadUInt32BigEndian(source[16..]),
        BinaryPrimitives.ReadUInt32BigEndian(source[20..]));
}

/// <summary>
/// An RTCP Sender Report (RFC 3550 §6.4.1). The <see cref="NtpTimestamp"/> / <see cref="RtpTimestamp"/>
/// pair is the wall-clock-to-RTP mapping a receiver uses to align streams (the anchor the playout clock
/// consumes when abs-capture-time is absent).
/// </summary>
public readonly record struct RtcpSenderReport(
    uint Ssrc,
    ulong NtpTimestamp,
    uint RtpTimestamp,
    uint SenderPacketCount,
    uint SenderOctetCount,
    IReadOnlyList<RtcpReportBlock> ReportBlocks)
{
    /// <summary>Serializes the sender report, returning the number of bytes written.</summary>
    public int Write(Span<byte> destination)
    {
        int count = ReportBlocks.Count;
        int length = 28 + (count * RtcpReportBlock.Length);
        destination[0] = (byte)(0x80 | count); // V=2, P=0, RC
        destination[1] = (byte)RtcpPacketType.SenderReport;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)((length / 4) - 1));
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], Ssrc);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], NtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], RtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(destination[20..], SenderPacketCount);
        BinaryPrimitives.WriteUInt32BigEndian(destination[24..], SenderOctetCount);
        for (int i = 0; i < count; i++)
        {
            ReportBlocks[i].Write(destination[(28 + (i * RtcpReportBlock.Length))..]);
        }

        return length;
    }

    /// <summary>Parses the first Sender Report in an RTCP compound packet, if present.</summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out RtcpSenderReport report)
    {
        report = default;
        foreach (RtcpElement element in RtcpCompound.Enumerate(packet))
        {
            if (element.PacketType != RtcpPacketType.SenderReport || element.Body.Length < 24)
            {
                continue;
            }

            ReadOnlySpan<byte> body = element.Body;
            var blocks = new RtcpReportBlock[element.ReportCount];
            for (int i = 0; i < blocks.Length && 24 + ((i + 1) * RtcpReportBlock.Length) <= body.Length; i++)
            {
                blocks[i] = RtcpReportBlock.Read(body[(24 + (i * RtcpReportBlock.Length))..]);
            }

            report = new RtcpSenderReport(
                BinaryPrimitives.ReadUInt32BigEndian(body),
                BinaryPrimitives.ReadUInt64BigEndian(body[4..]),
                BinaryPrimitives.ReadUInt32BigEndian(body[12..]),
                BinaryPrimitives.ReadUInt32BigEndian(body[16..]),
                BinaryPrimitives.ReadUInt32BigEndian(body[20..]),
                blocks);
            return true;
        }

        return false;
    }
}

/// <summary>An RTCP Receiver Report (RFC 3550 §6.4.2).</summary>
public readonly record struct RtcpReceiverReport(uint Ssrc, IReadOnlyList<RtcpReportBlock> ReportBlocks)
{
    /// <summary>Serializes the receiver report, returning the number of bytes written.</summary>
    public int Write(Span<byte> destination)
    {
        int count = ReportBlocks.Count;
        int length = 8 + (count * RtcpReportBlock.Length);
        destination[0] = (byte)(0x80 | count);
        destination[1] = (byte)RtcpPacketType.ReceiverReport;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)((length / 4) - 1));
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], Ssrc);
        for (int i = 0; i < count; i++)
        {
            ReportBlocks[i].Write(destination[(8 + (i * RtcpReportBlock.Length))..]);
        }

        return length;
    }

    /// <summary>Parses the first Receiver Report in an RTCP compound packet, if present.</summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out RtcpReceiverReport report)
    {
        report = default;
        foreach (RtcpElement element in RtcpCompound.Enumerate(packet))
        {
            if (element.PacketType != RtcpPacketType.ReceiverReport || element.Body.Length < 4)
            {
                continue;
            }

            var blocks = new RtcpReportBlock[element.ReportCount];
            for (int i = 0; i < blocks.Length && 4 + ((i + 1) * RtcpReportBlock.Length) <= element.Body.Length; i++)
            {
                blocks[i] = RtcpReportBlock.Read(element.Body[(4 + (i * RtcpReportBlock.Length))..]);
            }

            report = new RtcpReceiverReport(BinaryPrimitives.ReadUInt32BigEndian(element.Body), blocks);
            return true;
        }

        return false;
    }
}
