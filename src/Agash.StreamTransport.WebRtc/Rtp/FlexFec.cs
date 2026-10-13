using System.Buffers.Binary;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Rtp;

/// <summary>A source RTP packet a FEC repair packet protects: the recovery-relevant header fields + the bytes after the fixed 12-byte header.</summary>
/// <param name="SequenceNumber">RTP sequence number.</param>
/// <param name="HeaderBits">The P, X and CC fields: the low six bits of the RTP header's first octet.</param>
/// <param name="MarkerAndPayloadType">The RTP header's second octet: the marker bit and the payload type.</param>
/// <param name="Timestamp">RTP timestamp.</param>
/// <param name="BodyAfterHeader">Everything after the fixed 12-byte RTP header (CSRC/extensions/payload).</param>
public readonly record struct FecSourcePacket(
    ushort SequenceNumber,
    byte HeaderBits,
    byte MarkerAndPayloadType,
    uint Timestamp,
    ReadOnlyMemory<byte> BodyAfterHeader
);

/// <summary>A FlexFEC-recovered source packet.</summary>
/// <param name="SequenceNumber">Recovered RTP sequence number (SN base + mask position).</param>
/// <param name="HeaderBits">Recovered P, X and CC fields.</param>
/// <param name="MarkerAndPayloadType">Recovered marker bit and payload type, as the RTP header's second octet.</param>
/// <param name="Timestamp">Recovered RTP timestamp.</param>
/// <param name="BodyAfterHeader">Recovered bytes after the fixed 12-byte RTP header.</param>
public readonly record struct FecRecoveredPacket(
    ushort SequenceNumber,
    byte HeaderBits,
    byte MarkerAndPayloadType,
    uint Timestamp,
    byte[] BodyAfterHeader
);

/// <summary>
/// FlexFEC (RFC 8627) over a single source SSRC with the flexible mask (R=0, F=0): a repair packet
/// XOR-protects any set of source packets within 110 of its sequence number base, and recovers one lost
/// packet from the repair plus the others it protects. The mask is 15, 46 or 110 bits, the shortest that
/// covers the packets. The repair is its own RTP stream (its own SSRC and payload type, the protected SSRC
/// in its CSRC list), negotiated as the <c>flexfec</c> codec with an <c>a=ssrc-group:FEC-FR</c> pairing the
/// two SSRCs (RFC 8627 section 5). Loss repair without a retransmission round trip.
/// </summary>
public static class FlexFec
{
    /// <summary>The encoding name: <c>flexfec</c> (RFC 8627 section 5.1.2).</summary>
    public const string EncodingName = "flexfec";

    /// <summary>
    /// The repair window offered, in microseconds (RFC 8627 section 5.1.2): how far a repair packet may
    /// trail the oldest source packet it protects. One second covers a group spanning several frames at
    /// low frame rates; the receiver keeps 256 protected packets for it.
    /// </summary>
    public const int RepairWindowMicros = 1_000_000;

    /// <summary>The most source packets one repair can protect: the longest mask's 110 bits.</summary>
    public const int MaxProtected = 110;

    /// <summary>The longest FEC header: the 10 fixed octets and a 110-bit mask with its two k bits.</summary>
    public const int MaxHeaderLength = 24;

    /// <summary>What a repair packet adds beyond the largest packet it protects, at most: its CSRC and FEC header.</summary>
    public const int MaxRepairOverhead = CsrcLength + MaxHeaderLength;

    // The protected SSRC, the repair's one CSRC.
    internal const int CsrcLength = 4;

    // R, F, P, X, CC, M, PT recovery, length recovery, TS recovery, SN base.
    private const int FixedHeaderLength = 10;

    // The mask blocks: k and 15 bits, k and 31 bits, then 64 bits (RFC 8627 section 4.2.2.1).
    private const int ShortMask = 15;
    private const int MiddleMask = 46;

    /// <summary>Whether a codec is FlexFEC.</summary>
    /// <param name="codec">The codec.</param>
    /// <returns>True for <c>flexfec</c>.</returns>
    public static bool IsFlexFec(SdpCodec codec) =>
        string.Equals(codec.EncodingName, EncodingName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The FlexFEC codec for a video section.</summary>
    /// <param name="payloadType">Its payload type.</param>
    /// <param name="clockRate">The protected stream's clock rate, as RFC 8627 recommends.</param>
    /// <returns>The codec.</returns>
    public static SdpCodec Codec(int payloadType, int clockRate = 90_000) =>
        new(
            payloadType,
            EncodingName,
            clockRate,
            null,
            FormattableString.Invariant($"repair-window={RepairWindowMicros}"),
            []
        );

    /// <summary>The FEC header's length for a repair whose packets reach <paramref name="span"/> past its base.</summary>
    /// <param name="span">One more than the largest offset from the sequence number base: 1 to 110.</param>
    /// <returns>12, 16 or 24 octets.</returns>
    public static int HeaderLength(int span) =>
        span switch
        {
            <= 0 or > MaxProtected => throw new ArgumentOutOfRangeException(nameof(span)),
            <= ShortMask => FixedHeaderLength + 2,
            <= MiddleMask => FixedHeaderLength + 6,
            _ => FixedHeaderLength + 14,
        };

    /// <summary>The mask bit for the packet <paramref name="offset"/> past the base; bit 0 is the most significant.</summary>
    internal static UInt128 MaskBit(int offset) => UInt128.One << (127 - offset);

    // The mask with its first protected packet, the most significant bit set, cleared.
    private static UInt128 WithoutFirst(UInt128 mask) =>
        mask ^ MaskBit((int)UInt128.LeadingZeroCount(mask));

    /// <summary>
    /// Writes the FEC header for a repair protecting the packets in <paramref name="mask"/>, ending at the end of
    /// <paramref name="destination"/>, which is <see cref="HeaderLength"/> long.
    /// </summary>
    /// <param name="destination">The header's octets.</param>
    /// <param name="headerBits">The XOR of the protected packets' P, X and CC fields.</param>
    /// <param name="markerAndPayloadType">The XOR of their second octets.</param>
    /// <param name="length">The XOR of their lengths after the fixed header.</param>
    /// <param name="timestamp">The XOR of their timestamps.</param>
    /// <param name="sequenceBase">The lowest protected sequence number.</param>
    /// <param name="mask">The protected offsets, bit 127 for offset 0.</param>
    internal static void WriteHeader(
        Span<byte> destination,
        byte headerBits,
        byte markerAndPayloadType,
        ushort length,
        uint timestamp,
        ushort sequenceBase,
        UInt128 mask
    )
    {
        destination[0] = (byte)(headerBits & 0x3F);
        destination[1] = markerAndPayloadType;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], length);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], timestamp);
        BinaryPrimitives.WriteUInt16BigEndian(destination[8..], sequenceBase);

        // Each block's k bit says another block follows.
        bool middle = destination.Length > FixedHeaderLength + 2;
        bool full = destination.Length > FixedHeaderLength + 6;
        ushort first = (ushort)((ushort)(mask >> 113) & 0x7FFF);
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[FixedHeaderLength..],
            (ushort)(first | (middle ? 0x8000 : 0))
        );
        if (middle)
        {
            uint second = (uint)(mask >> 82) & 0x7FFF_FFFF;
            BinaryPrimitives.WriteUInt32BigEndian(
                destination[(FixedHeaderLength + 2)..],
                second | (full ? 0x8000_0000u : 0)
            );
        }

        if (full)
        {
            BinaryPrimitives.WriteUInt64BigEndian(
                destination[(FixedHeaderLength + 6)..],
                (ulong)(mask >> 18)
            );
        }
    }

    /// <summary>
    /// Writes a repair RTP packet: the RTP header with the protected SSRC as its one CSRC, then the FEC
    /// header and parity (RFC 8627 section 4.2.1).
    /// </summary>
    /// <param name="destination">Where it goes: at least 16 octets more than <paramref name="repair"/>.</param>
    /// <param name="payloadType">The negotiated FlexFEC payload type.</param>
    /// <param name="sequenceNumber">The repair stream's next sequence number.</param>
    /// <param name="timestamp">The repair's send time on the protected stream's clock.</param>
    /// <param name="fecSsrc">The repair stream's SSRC.</param>
    /// <param name="protectedSsrc">The protected media's SSRC.</param>
    /// <param name="repair">The FEC header and parity.</param>
    /// <returns>The packet's length.</returns>
    public static int WriteRepairPacket(
        Span<byte> destination,
        byte payloadType,
        ushort sequenceNumber,
        uint timestamp,
        uint fecSsrc,
        uint protectedSsrc,
        ReadOnlySpan<byte> repair
    )
    {
        destination[0] = 0x80 | 1; // Version 2, one CSRC.
        destination[1] = (byte)(payloadType & 0x7F); // The marker is unused and 0.
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], sequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], fecSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..], protectedSsrc);
        repair.CopyTo(destination[(RtpPacket.FixedHeaderLength + CsrcLength)..]);
        return RtpPacket.FixedHeaderLength + CsrcLength + repair.Length;
    }

    /// <summary>
    /// Builds the repair body protecting <paramref name="sources"/>: 1 to 110 packets within 110 of the
    /// first one's sequence number, in any order after it. The returned bytes are the repair packet's payload
    /// (FEC header and parity).
    /// </summary>
    /// <param name="sources">The protected packets; the first is the sequence number base.</param>
    /// <returns>The repair body.</returns>
    public static byte[] BuildRepair(IReadOnlyList<FecSourcePacket> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count is 0 or > MaxProtected)
        {
            throw new ArgumentException(
                $"FlexFEC protects 1..{MaxProtected} packets, got {sources.Count}.",
                nameof(sources)
            );
        }

        ushort snBase = sources[0].SequenceNumber;
        int maxBody = 0;
        int span = 0;
        UInt128 mask = 0;
        byte headerBits = 0;
        byte second = 0;
        uint timestamp = 0;
        ushort length = 0;
        foreach (FecSourcePacket s in sources)
        {
            int offset = (ushort)(s.SequenceNumber - snBase);
            if (offset >= MaxProtected)
            {
                throw new ArgumentException(
                    $"FlexFEC protects packets less than {MaxProtected} past the first; got offset {offset}.",
                    nameof(sources)
                );
            }

            mask |= MaskBit(offset);
            span = Math.Max(span, offset + 1);
            headerBits ^= s.HeaderBits;
            second ^= s.MarkerAndPayloadType;
            timestamp ^= s.Timestamp;
            length ^= (ushort)s.BodyAfterHeader.Length;
            maxBody = Math.Max(maxBody, s.BodyAfterHeader.Length);
        }

        int header = HeaderLength(span);
        byte[] fec = new byte[header + maxBody];
        WriteHeader(fec.AsSpan(0, header), headerBits, second, length, timestamp, snBase, mask);
        Span<byte> repair = fec.AsSpan(header);
        foreach (FecSourcePacket s in sources)
        {
            Parity.Xor(repair[..s.BodyAfterHeader.Length], s.BodyAfterHeader.Span);
        }

        return fec;
    }

    /// <summary>
    /// Reads a repair body's header: the recovery fields, the sequence number base, the protected offsets and
    /// where the parity starts.
    /// </summary>
    /// <param name="fecBody">The repair packet's payload.</param>
    /// <param name="header">What the header says.</param>
    /// <returns>Whether it is a well-formed flexible-mask repair (R=0, F=0).</returns>
    internal static bool TryReadHeader(ReadOnlySpan<byte> fecBody, out RepairHeader header)
    {
        header = default;
        if (fecBody.Length < FixedHeaderLength + 2 || (fecBody[0] & 0xC0) != 0)
        {
            return false;
        }

        ushort first = BinaryPrimitives.ReadUInt16BigEndian(fecBody[FixedHeaderLength..]);
        UInt128 mask = (UInt128)(first & 0x7FFFu) << 113;
        int length = FixedHeaderLength + 2;
        if ((first & 0x8000) != 0)
        {
            if (fecBody.Length < FixedHeaderLength + 6)
            {
                return false;
            }

            uint second = BinaryPrimitives.ReadUInt32BigEndian(fecBody[(FixedHeaderLength + 2)..]);
            mask |= (UInt128)(second & 0x7FFF_FFFFu) << 82;
            length = FixedHeaderLength + 6;
            if ((second & 0x8000_0000u) != 0)
            {
                if (fecBody.Length < FixedHeaderLength + 14)
                {
                    return false;
                }

                ulong third = BinaryPrimitives.ReadUInt64BigEndian(
                    fecBody[(FixedHeaderLength + 6)..]
                );
                mask |= (UInt128)third << 18;
                length = FixedHeaderLength + 14;
            }
        }

        header = new RepairHeader(
            (byte)(fecBody[0] & 0x3F),
            fecBody[1],
            BinaryPrimitives.ReadUInt16BigEndian(fecBody[2..]),
            BinaryPrimitives.ReadUInt32BigEndian(fecBody[4..]),
            BinaryPrimitives.ReadUInt16BigEndian(fecBody[8..]),
            mask,
            length
        );
        return true;
    }

    /// <summary>
    /// Recovers the one lost packet a repair protects. <paramref name="lookup"/> returns a protected packet
    /// by sequence number, or null if it was lost. Null when none is missing, or two or more are, which one
    /// repair cannot recover.
    /// </summary>
    /// <param name="fecBody">The repair packet's payload.</param>
    /// <param name="lookup">The protected packets received.</param>
    /// <returns>The recovered packet, or null.</returns>
    public static FecRecoveredPacket? TryRecover(
        ReadOnlySpan<byte> fecBody,
        Func<ushort, FecSourcePacket?> lookup
    )
    {
        ArgumentNullException.ThrowIfNull(lookup);
        if (!TryReadHeader(fecBody, out RepairHeader header))
        {
            return null;
        }

        // First which one is missing, so a repair with nothing to recover costs no parity work.
        ushort? missing = null;
        for (UInt128 rest = header.Mask; rest != 0; rest = WithoutFirst(rest))
        {
            ushort sequence = (ushort)(header.SequenceBase + (int)UInt128.LeadingZeroCount(rest));
            if (lookup(sequence) is not null)
            {
                continue;
            }

            if (missing is not null)
            {
                return null;
            }

            missing = sequence;
        }

        if (missing is not { } recoveredSequence)
        {
            return null;
        }

        byte headerBits = header.HeaderBits;
        byte second = header.MarkerAndPayloadType;
        uint timestamp = header.Timestamp;
        ushort length = header.Length;
        ReadOnlySpan<byte> parity = fecBody[header.HeaderLength..];
        for (UInt128 rest = header.Mask; rest != 0; rest = WithoutFirst(rest))
        {
            ushort sequence = (ushort)(header.SequenceBase + (int)UInt128.LeadingZeroCount(rest));
            if (sequence != recoveredSequence && lookup(sequence) is { } s)
            {
                headerBits ^= s.HeaderBits;
                second ^= s.MarkerAndPayloadType;
                timestamp ^= s.Timestamp;
                length ^= (ushort)s.BodyAfterHeader.Length;
            }
        }

        if (length > parity.Length)
        {
            return null; // Inconsistent with what was received.
        }

        byte[] body = parity[..length].ToArray();
        for (UInt128 rest = header.Mask; rest != 0; rest = WithoutFirst(rest))
        {
            ushort sequence = (ushort)(header.SequenceBase + (int)UInt128.LeadingZeroCount(rest));
            if (sequence != recoveredSequence && lookup(sequence) is { } s)
            {
                ReadOnlySpan<byte> other = s.BodyAfterHeader.Span;
                Parity.Xor(body, other[..Math.Min(body.Length, other.Length)]);
            }
        }

        return new FecRecoveredPacket(
            recoveredSequence,
            (byte)(headerBits & 0x3F),
            second,
            timestamp,
            body
        );
    }
}

/// <summary>A repair's FEC header, read.</summary>
/// <param name="HeaderBits">P, X and CC recovery.</param>
/// <param name="MarkerAndPayloadType">M and PT recovery.</param>
/// <param name="Length">Length recovery.</param>
/// <param name="Timestamp">TS recovery.</param>
/// <param name="SequenceBase">The lowest protected sequence number.</param>
/// <param name="Mask">The protected offsets, bit 127 for offset 0.</param>
/// <param name="HeaderLength">Where the parity starts.</param>
internal readonly record struct RepairHeader(
    byte HeaderBits,
    byte MarkerAndPayloadType,
    ushort Length,
    uint Timestamp,
    ushort SequenceBase,
    UInt128 Mask,
    int HeaderLength
);
