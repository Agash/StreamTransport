using System.Buffers;
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

    /// <summary>RTPFB formats for TMMBR and TMMBN (RFC 5104 section 4.2).</summary>
    private const int FmtTmmbr = 3;
    private const int FmtTmmbn = 4;

    // RFC 5104 section 4.2.1.1: a 17-bit mantissa under a 6-bit exponent.
    private const long MantissaMax = 0x1FFFF;

    /// <summary>
    /// Builds a Transport-layer feedback Generic NACK (PT 205, FMT 1) requesting retransmission of the
    /// given sequence numbers, packing them into PID/BLP fields. Returns the bytes written.
    /// </summary>
    public static int BuildNack(
        Span<byte> destination,
        uint senderSsrc,
        uint mediaSsrc,
        ReadOnlySpan<ushort> lostSequences
    )
    {
        int fciOffset = 12;
        int i = 0;
        ushort[]? rented =
            lostSequences.Length <= 256
                ? null
                : ArrayPool<ushort>.Shared.Rent(lostSequences.Length);
        Span<ushort> sorted = rented is null
            ? stackalloc ushort[lostSequences.Length]
            : rented.AsSpan(0, lostSequences.Length);
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

        WriteFeedbackHeader(
            destination,
            RtcpPacketType.TransportFeedback,
            FmtNackOrPli,
            senderSsrc,
            mediaSsrc,
            fciOffset
        );
        if (rented is not null)
        {
            ArrayPool<ushort>.Shared.Return(rented);
        }

        return fciOffset;
    }

    /// <summary>
    /// Builds a Temporary Maximum Media Stream Bit Rate Request (RFC 5104 section 4.2.1, RTPFB FMT 3) asking
    /// the sender of one media stream to keep under a bit rate. Returns the bytes written (20).
    /// </summary>
    /// <param name="destination">Where it goes.</param>
    /// <param name="senderSsrc">The requesting source.</param>
    /// <param name="mediaSsrc">The stream to limit.</param>
    /// <param name="bitsPerSecond">The limit, rounded down to what the field holds.</param>
    /// <param name="overhead">The per-packet overhead the requester counts in the limit, 0 to 511 bytes.</param>
    /// <returns>The bytes written.</returns>
    public static int BuildTmmbr(
        Span<byte> destination,
        uint senderSsrc,
        uint mediaSsrc,
        long bitsPerSecond,
        int overhead
    ) =>
        BuildTmmb(
            destination,
            FmtTmmbr,
            senderSsrc,
            [new TmmbEntry(mediaSsrc, bitsPerSecond, overhead)]
        );

    /// <summary>
    /// Builds a TMMBN (RFC 5104 section 4.2.2, RTPFB FMT 4): the bounding set a media sender now keeps to,
    /// each entry naming the owner of the request.
    /// </summary>
    /// <param name="destination">Where it goes.</param>
    /// <param name="senderSsrc">The media sender.</param>
    /// <param name="bounding">The bounding set.</param>
    /// <returns>The bytes written.</returns>
    public static int BuildTmmbn(
        Span<byte> destination,
        uint senderSsrc,
        ReadOnlySpan<TmmbEntry> bounding
    ) => BuildTmmb(destination, FmtTmmbn, senderSsrc, bounding);

    /// <summary>Reads a TMMBR element, appending its entries.</summary>
    /// <param name="element">An element of a compound packet.</param>
    /// <param name="senderSsrc">The requesting source.</param>
    /// <param name="entries">Where the entries go.</param>
    /// <returns>Whether the element is a TMMBR.</returns>
    public static bool TryReadTmmbr(
        in RtcpElement element,
        out uint senderSsrc,
        List<TmmbEntry> entries
    ) => TryReadTmmb(element, FmtTmmbr, out senderSsrc, entries);

    /// <summary>Reads a TMMBN element, appending its bounding set.</summary>
    /// <param name="element">An element of a compound packet.</param>
    /// <param name="senderSsrc">The media sender.</param>
    /// <param name="entries">Where the entries go.</param>
    /// <returns>Whether the element is a TMMBN.</returns>
    public static bool TryReadTmmbn(
        in RtcpElement element,
        out uint senderSsrc,
        List<TmmbEntry> entries
    ) => TryReadTmmb(element, FmtTmmbn, out senderSsrc, entries);

    // RFC 5104 section 4.2.1.1: SSRC, then MxTBR Exp (6), Mantissa (17), Measured Overhead (9).
    private static int BuildTmmb(
        Span<byte> destination,
        int fmt,
        uint senderSsrc,
        ReadOnlySpan<TmmbEntry> entries
    )
    {
        int at = 12;
        foreach (TmmbEntry entry in entries)
        {
            long rate = Math.Max(0, entry.BitsPerSecond);
            int exponent = 0;
            while ((rate >> exponent) > MantissaMax && exponent < 63)
            {
                exponent++;
            }

            uint mantissa = (uint)Math.Min(rate >> exponent, MantissaMax);
            BinaryPrimitives.WriteUInt32BigEndian(destination[at..], entry.Ssrc);
            BinaryPrimitives.WriteUInt32BigEndian(
                destination[(at + 4)..],
                ((uint)exponent << 26) | (mantissa << 9) | (uint)Math.Clamp(entry.Overhead, 0, 511)
            );
            at += 8;
        }

        // Section 4.2.1: the media source field is 0; the entries name the streams.
        WriteFeedbackHeader(destination, RtcpPacketType.TransportFeedback, fmt, senderSsrc, 0, at);
        return at;
    }

    private static bool TryReadTmmb(
        in RtcpElement element,
        int fmt,
        out uint senderSsrc,
        List<TmmbEntry> entries
    )
    {
        ArgumentNullException.ThrowIfNull(entries);
        senderSsrc = 0;
        if (
            element.PacketType != RtcpPacketType.TransportFeedback
            || element.ReportCount != fmt
            || element.Body.Length < 8
        )
        {
            return false;
        }

        senderSsrc = BinaryPrimitives.ReadUInt32BigEndian(element.Body);
        for (int o = 8; o + 8 <= element.Body.Length; o += 8)
        {
            uint word = BinaryPrimitives.ReadUInt32BigEndian(element.Body[(o + 4)..]);
            int exponent = (int)(word >> 26);
            long mantissa = (word >> 9) & MantissaMax;
            long rate = exponent >= 46 && mantissa > 0 ? long.MaxValue : mantissa << exponent;
            entries.Add(
                new TmmbEntry(
                    BinaryPrimitives.ReadUInt32BigEndian(element.Body[o..]),
                    rate,
                    (int)(word & 0x1FF)
                )
            );
        }

        return true;
    }

    /// <summary>
    /// Reads one Generic NACK element (RTPFB, FMT 1), appending the lost sequence numbers it requests to
    /// <paramref name="lost"/>. A compound packet may carry several, for different SSRCs (RFC 8108).
    /// </summary>
    /// <param name="element">An element of a compound packet.</param>
    /// <param name="mediaSsrc">The media SSRC the NACK is about.</param>
    /// <param name="lost">Where the requested sequence numbers go.</param>
    /// <returns>Whether the element is a Generic NACK.</returns>
    public static bool TryReadNack(in RtcpElement element, out uint mediaSsrc, List<ushort> lost)
    {
        ArgumentNullException.ThrowIfNull(lost);
        mediaSsrc = 0;
        if (
            element.PacketType != RtcpPacketType.TransportFeedback
            || element.ReportCount != FmtNackOrPli
            || element.Body.Length < 8
        )
        {
            return false;
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

    /// <summary>Reads one Picture Loss Indication element (PSFB, FMT 1).</summary>
    /// <param name="element">An element of a compound packet.</param>
    /// <param name="mediaSsrc">The media SSRC a keyframe is asked for.</param>
    /// <returns>Whether the element is a PLI.</returns>
    public static bool TryReadPli(in RtcpElement element, out uint mediaSsrc)
    {
        mediaSsrc = 0;
        if (
            element.PacketType != RtcpPacketType.PayloadFeedback
            || element.ReportCount != FmtNackOrPli
            || element.Body.Length < 8
        )
        {
            return false;
        }

        mediaSsrc = BinaryPrimitives.ReadUInt32BigEndian(element.Body[4..]);
        return true;
    }

    /// <summary>
    /// Reads the requests of one Full Intra Request element (PSFB, FMT 4, RFC 5104 section 4.3.1): each
    /// FCI entry names a media sender and a command sequence number.
    /// </summary>
    /// <param name="element">An element of a compound packet.</param>
    /// <param name="senderSsrc">The SSRC of the request's sender.</param>
    /// <param name="requests">Where the target SSRCs and sequence numbers go.</param>
    /// <returns>Whether the element is a FIR.</returns>
    public static bool TryReadFir(
        in RtcpElement element,
        out uint senderSsrc,
        List<(uint TargetSsrc, byte Sequence)> requests
    )
    {
        ArgumentNullException.ThrowIfNull(requests);
        senderSsrc = 0;
        if (
            element.PacketType != RtcpPacketType.PayloadFeedback
            || element.ReportCount != FmtFir
            || element.Body.Length < 8
        )
        {
            return false;
        }

        senderSsrc = BinaryPrimitives.ReadUInt32BigEndian(element.Body);
        for (int o = 8; o + 8 <= element.Body.Length; o += 8)
        {
            requests.Add(
                (BinaryPrimitives.ReadUInt32BigEndian(element.Body[o..]), element.Body[o + 4])
            );
        }

        return true;
    }

    /// <summary>Builds a Picture Loss Indication (PT 206, FMT 1). Returns the bytes written (12).</summary>
    public static int BuildPli(Span<byte> destination, uint senderSsrc, uint mediaSsrc)
    {
        WriteFeedbackHeader(
            destination,
            RtcpPacketType.PayloadFeedback,
            FmtNackOrPli,
            senderSsrc,
            mediaSsrc,
            12
        );
        return 12;
    }

    /// <summary>Builds a Full Intra Request (PT 206, FMT 4) for one media SSRC with a command sequence number.</summary>
    public static int BuildFir(
        Span<byte> destination,
        uint senderSsrc,
        uint targetSsrc,
        byte commandSequence
    )
    {
        // FCI: target SSRC (4) + seq nr (1) + reserved (3).
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..], targetSsrc);
        destination[16] = commandSequence;
        destination[17] = 0;
        destination[18] = 0;
        destination[19] = 0;
        WriteFeedbackHeader(
            destination,
            RtcpPacketType.PayloadFeedback,
            FmtFir,
            senderSsrc,
            mediaSsrc: 0,
            20
        );
        return 20;
    }

    private static void WriteFeedbackHeader(
        Span<byte> destination,
        RtcpPacketType type,
        int fmt,
        uint senderSsrc,
        uint mediaSsrc,
        int totalLength
    )
    {
        destination[0] = (byte)(0x80 | (fmt & 0x1F)); // V=2, P=0, FMT
        destination[1] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)((totalLength / 4) - 1));
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], senderSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], mediaSsrc);
    }
}

/// <summary>One TMMBR or TMMBN entry (RFC 5104 section 4.2.1.1).</summary>
/// <param name="Ssrc">The stream a request limits, or the owner of a bounding-set entry.</param>
/// <param name="BitsPerSecond">The maximum bit rate.</param>
/// <param name="Overhead">The per-packet overhead counted in it, in bytes.</param>
public readonly record struct TmmbEntry(uint Ssrc, long BitsPerSecond, int Overhead);
