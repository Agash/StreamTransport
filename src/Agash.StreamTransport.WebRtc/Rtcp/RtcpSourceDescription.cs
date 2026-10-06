using System.Buffers.Binary;
using System.Text;

namespace Agash.StreamTransport.WebRtc.Rtcp;

/// <summary>
/// RTCP source description and goodbye packets (RFC 3550 sections 6.5 and 6.6): every compound report
/// carries the sender's CNAME, and a closing endpoint says goodbye for each of its sources.
/// </summary>
public static class RtcpSourceDescription
{
    private const byte CnameItem = 1;

    /// <summary>The bytes an SDES packet with one CNAME chunk per source takes.</summary>
    /// <param name="sources">How many sources it describes.</param>
    /// <param name="cname">The canonical name.</param>
    /// <returns>The packet length.</returns>
    public static int CnameLength(int sources, string cname) =>
        4 + (sources * ChunkLength(Encoding.UTF8.GetByteCount(cname)));

    /// <summary>Writes an SDES packet giving each source the canonical name.</summary>
    /// <param name="destination">Where it goes, at least <see cref="CnameLength"/> bytes.</param>
    /// <param name="sources">The sources, 1 to 31.</param>
    /// <param name="cname">The canonical name, at most 255 bytes of UTF-8.</param>
    /// <returns>The bytes written.</returns>
    public static int WriteCname(Span<byte> destination, ReadOnlySpan<uint> sources, string cname)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sources.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sources.Length, 31);
        int nameLength = Encoding.UTF8.GetByteCount(cname);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nameLength, 255, nameof(cname));
        int length = CnameLength(sources.Length, cname);
        destination[..length].Clear();
        destination[0] = (byte)(0x80 | sources.Length);
        destination[1] = (byte)RtcpPacketType.SourceDescription;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)((length / 4) - 1));
        int offset = 4;
        foreach (uint source in sources)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination[offset..], source);
            destination[offset + 4] = CnameItem;
            destination[offset + 5] = (byte)nameLength;
            _ = Encoding.UTF8.GetBytes(cname, destination[(offset + 6)..]);

            // The item list ends with a null octet, then pads to a 32-bit boundary (the clear did both).
            offset += ChunkLength(nameLength);
        }

        return length;
    }

    /// <summary>Writes a BYE packet for the sources.</summary>
    /// <param name="destination">Where it goes, at least 4 + 4 per source bytes.</param>
    /// <param name="sources">The sources leaving, 1 to 31.</param>
    /// <returns>The bytes written.</returns>
    public static int WriteGoodbye(Span<byte> destination, ReadOnlySpan<uint> sources)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sources.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sources.Length, 31);
        int length = 4 + (sources.Length * 4);
        destination[0] = (byte)(0x80 | sources.Length);
        destination[1] = (byte)RtcpPacketType.Goodbye;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)((length / 4) - 1));
        for (int i = 0; i < sources.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination[(4 + (i * 4))..], sources[i]);
        }

        return length;
    }

    // SSRC, type, length, the name, at least one null octet, padded to 32 bits.
    private static int ChunkLength(int nameLength) => (4 + 2 + nameLength + 1 + 3) & ~3;
}
