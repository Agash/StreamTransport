using System.Buffers.Binary;

namespace Agash.StreamTransport.WebRtc.Rtcp;

/// <summary>
/// Builds and parses RTCP feedback messages used for loss recovery: Generic NACK (RFC 4585 §6.2.1),
/// Picture Loss Indication (RFC 4585 §6.3.1), and Full Intra Request (RFC 5104 §4.3.1). Allocation-light:
/// building writes into a caller span; NACK parsing yields sequence numbers into a caller list.
/// </summary>
public static class RtcpFeedback
{
    /// <summary>RTPFB / PSFB format for Generic NACK and PLI.</summary>
    private const int FmtNackOrPli = 1;

    /// <summary>PSFB format for Full Intra Request.</summary>
    private const int FmtFir = 4;

    /// <summary>
    /// Builds a Transport-layer feedback Generic NACK (PT 205, FMT 1) requesting retransmission of the
    /// given sequence numbers, packing them into PID/BLP fields. Returns the bytes written.
    /// </summary>
    public static int BuildNack(Span<byte> destination, uint senderSsrc, uint mediaSsrc, ReadOnlySpan<ushort> lostSequences)
    {
        int fciOffset = 12;
        int i = 0;
        Span<ushort> sorted = lostSequences.Length <= 256 ? stackalloc ushort[lostSequences.Length] : new ushort[lostSequences.Length];
        lostSequences.CopyTo(sorted);
        sorted.Sort();

        while (i < sorted.Length)
        {
            ushort pid = sorted[i];
            ushort blp = 0;
            int j = i + 1;
            while (j < sorted.Length)
            {
                int delta = (ushort)(sorted[j] - pid);
                if (delta is >= 1 and <= 16)
                {
                    blp |= (ushort)(1 << (delta - 1));
                    j++;
                }
                else
                {
                    break;
                }
            }

            BinaryPrimitives.WriteUInt16BigEndian(destination[fciOffset..], pid);
            BinaryPrimitives.WriteUInt16BigEndian(destination[(fciOffset + 2)..], blp);
            fciOffset += 4;
            i = j;
        }

        WriteFeedbackHeader(destination, RtcpPacketType.TransportFeedback, FmtNackOrPli, senderSsrc, mediaSsrc, fciOffset);
        return fciOffset;
    }

    /// <summary>Parses a Generic NACK, appending the requested lost sequence numbers to <paramref name="lost"/>.</summary>
    public static bool TryParseNack(ReadOnlySpan<byte> packet, out uint mediaSsrc, List<ushort> lost)
    {
        mediaSsrc = 0;
        foreach (RtcpElement element in RtcpCompound.Enumerate(packet))
        {
            if (element.PacketType != RtcpPacketType.TransportFeedback || element.ReportCount != FmtNackOrPli || element.Body.Length < 8)
            {
                continue;
            }

            mediaSsrc = BinaryPrimitives.ReadUInt32BigEndian(element.Body[4..]);
            for (int o = 8; o + 4 <= element.Body.Length; o += 4)
            {
                ushort pid = BinaryPrimitives.ReadUInt16BigEndian(element.Body[o..]);
                ushort blp = BinaryPrimitives.ReadUInt16BigEndian(element.Body[(o + 2)..]);
                lost.Add(pid);
                for (int bit = 0; bit < 16; bit++)
                {
                    if ((blp & (1 << bit)) != 0)
                    {
                        lost.Add((ushort)(pid + bit + 1));
                    }
                }
            }

            return true;
        }

        return false;
    }

    /// <summary>Builds a Picture Loss Indication (PT 206, FMT 1). Returns the bytes written (12).</summary>
    public static int BuildPli(Span<byte> destination, uint senderSsrc, uint mediaSsrc)
    {
        WriteFeedbackHeader(destination, RtcpPacketType.PayloadFeedback, FmtNackOrPli, senderSsrc, mediaSsrc, 12);
        return 12;
    }

    /// <summary>Returns true if the compound packet contains a PLI for <paramref name="mediaSsrc"/>.</summary>
    public static bool ContainsPli(ReadOnlySpan<byte> packet, out uint mediaSsrc)
    {
        mediaSsrc = 0;
        foreach (RtcpElement element in RtcpCompound.Enumerate(packet))
        {
            if (element.PacketType == RtcpPacketType.PayloadFeedback && element.ReportCount == FmtNackOrPli && element.Body.Length >= 8)
            {
                mediaSsrc = BinaryPrimitives.ReadUInt32BigEndian(element.Body[4..]);
                return true;
            }
        }

        return false;
    }

    /// <summary>Builds a Full Intra Request (PT 206, FMT 4) for one media SSRC with a command sequence number.</summary>
    public static int BuildFir(Span<byte> destination, uint senderSsrc, uint targetSsrc, byte commandSequence)
    {
        // FCI: target SSRC (4) + seq nr (1) + reserved (3).
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..], targetSsrc);
        destination[16] = commandSequence;
        destination[17] = 0;
        destination[18] = 0;
        destination[19] = 0;
        WriteFeedbackHeader(destination, RtcpPacketType.PayloadFeedback, FmtFir, senderSsrc, mediaSsrc: 0, 20);
        return 20;
    }

    private static void WriteFeedbackHeader(Span<byte> destination, RtcpPacketType type, int fmt, uint senderSsrc, uint mediaSsrc, int totalLength)
    {
        destination[0] = (byte)(0x80 | (fmt & 0x1F)); // V=2, P=0, FMT
        destination[1] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)((totalLength / 4) - 1));
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], senderSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], mediaSsrc);
    }
}
