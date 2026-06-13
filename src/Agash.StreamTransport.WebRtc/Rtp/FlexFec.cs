using System.Buffers.Binary;

namespace Agash.StreamTransport.WebRtc.Rtp;

/// <summary>A source RTP packet a FEC repair packet protects: the recovery-relevant header fields + the bytes after the fixed 12-byte header.</summary>
/// <param name="SequenceNumber">RTP sequence number.</param>
/// <param name="HeaderBits">The low byte-0 bits of the RTP header (P, X, CC, M) - bit layout <c>P X CC[4] M</c> in bits 5..0.</param>
/// <param name="PayloadType">The 7-bit payload type.</param>
/// <param name="Timestamp">RTP timestamp.</param>
/// <param name="BodyAfterHeader">Everything after the fixed 12-byte RTP header (CSRC/extensions/payload).</param>
public readonly record struct FecSourcePacket(
    ushort SequenceNumber, byte HeaderBits, byte PayloadType, uint Timestamp, ReadOnlyMemory<byte> BodyAfterHeader);

/// <summary>A FlexFEC-recovered source packet.</summary>
/// <param name="SequenceNumber">Recovered RTP sequence number (SN base + mask position).</param>
/// <param name="HeaderBits">Recovered P/X/CC/M bits.</param>
/// <param name="PayloadType">Recovered payload type.</param>
/// <param name="Timestamp">Recovered RTP timestamp.</param>
/// <param name="BodyAfterHeader">Recovered bytes after the fixed 12-byte RTP header.</param>
public readonly record struct FecRecoveredPacket(
    ushort SequenceNumber, byte HeaderBits, byte PayloadType, uint Timestamp, byte[] BodyAfterHeader);

/// <summary>
/// FlexFEC (RFC 8627, flexfec-03) over a single source SSRC with a 15-bit flexible mask: builds a repair
/// packet body that XOR-protects a run of up to 15 source packets, and recovers a single lost source packet
/// from the repair plus the other protected packets. The repair body is carried as the payload of a FlexFEC
/// RTP packet (its own SSRC + payload type, with the protected media SSRC as CSRC). Loss-masking without a
/// retransmit round trip - the right repair for a high-RTT/lossy uplink (the IRL contribution profile).
/// </summary>
public static class FlexFec
{
    /// <summary>Max source packets one 15-bit-mask repair packet can protect.</summary>
    public const int MaxProtected = 15;

    // FEC header (F=0, single SSRC, 15-bit mask) = 8 bytes fixed + 4 bytes (SN base + k + 15-bit mask).
    private const int HeaderLength = 12;

    /// <summary>
    /// Build the FlexFEC repair body protecting <paramref name="sources"/> (1..15 packets, ascending sequence
    /// numbers). The returned bytes are the FlexFEC RTP packet's payload (FEC header + repair payload).
    /// </summary>
    public static byte[] BuildRepair(IReadOnlyList<FecSourcePacket> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count is 0 or > MaxProtected)
        {
            throw new ArgumentException($"FlexFEC protects 1..{MaxProtected} packets, got {sources.Count}.", nameof(sources));
        }

        ushort snBase = sources[0].SequenceNumber;
        int maxBody = 0;
        ushort mask = 0;
        byte headerBitsXor = 0;
        byte ptXor = 0;
        uint tsXor = 0;
        ushort lengthXor = 0;
        foreach (FecSourcePacket s in sources)
        {
            int offset = (ushort)(s.SequenceNumber - snBase);
            if (offset >= MaxProtected)
            {
                throw new ArgumentException($"FlexFEC packet sequence span must be < {MaxProtected}; got offset {offset}.", nameof(sources));
            }

            mask |= (ushort)(1 << (14 - offset)); // j=0 is the most significant of the 15-bit mask.
            headerBitsXor ^= s.HeaderBits;
            ptXor ^= s.PayloadType;
            tsXor ^= s.Timestamp;
            lengthXor ^= (ushort)s.BodyAfterHeader.Length;
            maxBody = Math.Max(maxBody, s.BodyAfterHeader.Length);
        }

        byte[] fec = new byte[HeaderLength + maxBody];
        fec[0] = (byte)(headerBitsXor & 0x3F); // R=0,F=0 in the top two bits; P/X/CC/M recovery below.
        fec[1] = (byte)(ptXor & 0x7F);
        BinaryPrimitives.WriteUInt16BigEndian(fec.AsSpan(2), lengthXor);
        BinaryPrimitives.WriteUInt32BigEndian(fec.AsSpan(4), tsXor);
        BinaryPrimitives.WriteUInt16BigEndian(fec.AsSpan(8), snBase);
        BinaryPrimitives.WriteUInt16BigEndian(fec.AsSpan(10), (ushort)(mask & 0x7FFF)); // k=0 (last block) in the top bit.

        Span<byte> repair = fec.AsSpan(HeaderLength);
        foreach (FecSourcePacket s in sources)
        {
            ReadOnlySpan<byte> body = s.BodyAfterHeader.Span;
            for (int i = 0; i < body.Length; i++)
            {
                repair[i] ^= body[i];
            }
        }

        return fec;
    }

    /// <summary>
    /// Attempt to recover a single lost source packet from a repair body. <paramref name="lookup"/> returns a
    /// protected source packet by sequence number, or null if it was lost. Recovers when exactly one protected
    /// packet is missing; returns null otherwise (zero lost = nothing to do, two+ lost = unrecoverable here).
    /// </summary>
    public static FecRecoveredPacket? TryRecover(ReadOnlySpan<byte> fecBody, Func<ushort, FecSourcePacket?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        if (fecBody.Length < HeaderLength || (fecBody[0] & 0xC0) != 0)
        {
            return null; // not an F=0 flexible-mask repair packet.
        }

        byte headerBits = (byte)(fecBody[0] & 0x3F);
        byte pt = (byte)(fecBody[1] & 0x7F);
        ushort length = BinaryPrimitives.ReadUInt16BigEndian(fecBody[2..]);
        uint ts = BinaryPrimitives.ReadUInt32BigEndian(fecBody[4..]);
        ushort snBase = BinaryPrimitives.ReadUInt16BigEndian(fecBody[8..]);
        ushort mask = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(fecBody[10..]) & 0x7FFF);
        ReadOnlySpan<byte> repair = fecBody[HeaderLength..];

        ushort? missing = null;
        var present = new List<FecSourcePacket>(MaxProtected);
        for (int j = 0; j < 15; j++)
        {
            if ((mask & (1 << (14 - j))) == 0)
            {
                continue;
            }

            ushort seq = (ushort)(snBase + j);
            FecSourcePacket? source = lookup(seq);
            if (source is { } present_)
            {
                present.Add(present_);
            }
            else if (missing is null)
            {
                missing = seq;
            }
            else
            {
                return null; // more than one lost - cannot recover with a single repair packet.
            }
        }

        if (missing is not { } recoveredSeq)
        {
            return null; // nothing lost.
        }

        foreach (FecSourcePacket s in present)
        {
            headerBits ^= s.HeaderBits;
            pt ^= s.PayloadType;
            ts ^= s.Timestamp;
            length ^= (ushort)s.BodyAfterHeader.Length;
        }

        if (length > repair.Length)
        {
            return null; // corrupt / inconsistent.
        }

        byte[] body = repair[..length].ToArray();
        foreach (FecSourcePacket s in present)
        {
            ReadOnlySpan<byte> sBody = s.BodyAfterHeader.Span;
            int n = Math.Min(body.Length, sBody.Length);
            for (int i = 0; i < n; i++)
            {
                body[i] ^= sBody[i];
            }
        }

        return new FecRecoveredPacket(recoveredSeq, (byte)(headerBits & 0x3F), (byte)(pt & 0x7F), ts, body);
    }
}
