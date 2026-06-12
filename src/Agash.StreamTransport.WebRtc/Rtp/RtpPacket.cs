using System.Buffers.Binary;

namespace Agash.StreamTransport.WebRtc.Rtp;

/// <summary>
/// Reads and writes RTP packets (RFC 3550 §5.1) with one-byte header extensions (RFC 8285), including the
/// abs-capture-time extension used for A/V synchronization. Allocation-free: parsing borrows the caller's
/// buffer and writing composes into a caller-supplied span.
/// </summary>
public static class RtpPacket
{
    /// <summary>The fixed RTP header length (no CSRCs, no extension).</summary>
    public const int FixedHeaderLength = 12;

    /// <summary>The RFC 8285 one-byte header-extension profile identifier (<c>0xBEDE</c>).</summary>
    private const ushort OneByteExtensionProfile = 0xBEDE;

    /// <summary>
    /// Composes an RTP packet into <paramref name="destination"/>: the fixed header, an optional
    /// abs-capture-time one-byte extension, then the payload. Returns the total length written.
    /// </summary>
    public static int Write(
        Span<byte> destination,
        bool marker,
        byte payloadType,
        ushort sequenceNumber,
        uint timestamp,
        uint ssrc,
        ReadOnlySpan<byte> payload,
        int absCaptureTimeExtensionId = 0,
        ulong absCaptureTimeNtp = 0)
    {
        destination[0] = 0x80; // V=2, P=0, CC=0; X set below if there's an extension.
        destination[1] = (byte)((marker ? 0x80 : 0) | (payloadType & 0x7F));
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], sequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], ssrc);

        int offset = FixedHeaderLength;
        if (absCaptureTimeExtensionId is > 0 and < 15)
        {
            destination[0] |= 0x10; // X = 1
            BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], OneByteExtensionProfile);

            // One element: id||len-1 byte + 8 octets of UQ32.32 NTP. Pad the element area to a 32-bit word.
            const int elementLength = 8;
            int dataLength = 1 + elementLength;            // element header + value
            int paddedWords = (dataLength + 3) / 4;
            BinaryPrimitives.WriteUInt16BigEndian(destination[(offset + 2)..], (ushort)paddedWords);

            Span<byte> ext = destination[(offset + 4)..];
            ext[0] = (byte)((absCaptureTimeExtensionId << 4) | (elementLength - 1));
            BinaryPrimitives.WriteUInt64BigEndian(ext[1..], absCaptureTimeNtp);
            ext[(1 + elementLength)..(paddedWords * 4)].Clear();
            offset += 4 + (paddedWords * 4);
        }

        payload.CopyTo(destination[offset..]);
        return offset + payload.Length;
    }

    /// <summary>Parses an RTP packet, exposing its header fields and the payload span.</summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out RtpHeader header, out ReadOnlySpan<byte> payload)
    {
        header = default;
        payload = default;
        if (packet.Length < FixedHeaderLength || (packet[0] >> 6) != 2)
        {
            return false;
        }

        int csrcCount = packet[0] & 0x0F;
        bool hasExtension = (packet[0] & 0x10) != 0;
        bool marker = (packet[1] & 0x80) != 0;
        byte payloadType = (byte)(packet[1] & 0x7F);
        ushort sequenceNumber = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet[4..]);
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet[8..]);

        int offset = FixedHeaderLength + (csrcCount * 4);
        if (offset > packet.Length)
        {
            return false;
        }

        ulong? absCaptureNtp = null;
        if (hasExtension)
        {
            if (offset + 4 > packet.Length)
            {
                return false;
            }

            ushort profile = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            int words = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            int extDataStart = offset + 4;
            int extDataEnd = extDataStart + (words * 4);
            if (extDataEnd > packet.Length)
            {
                return false;
            }

            if (profile == OneByteExtensionProfile)
            {
                absCaptureNtp = FindAbsCaptureTime(packet[extDataStart..extDataEnd]);
            }

            offset = extDataEnd;
        }

        payload = packet[offset..];
        header = new RtpHeader(marker, payloadType, sequenceNumber, timestamp, ssrc, absCaptureNtp);
        return true;
    }

    private static ulong? FindAbsCaptureTime(ReadOnlySpan<byte> extension)
    {
        int i = 0;
        while (i < extension.Length)
        {
            byte b = extension[i];
            if (b == 0)
            {
                i++; // padding
                continue;
            }

            int length = (b & 0x0F) + 1;
            if (i + 1 + length > extension.Length)
            {
                break;
            }

            // abs-capture-time carries an 8- or 16-octet value; the first 8 octets are the UQ32.32 NTP time.
            if (length is 8 or 16)
            {
                return BinaryPrimitives.ReadUInt64BigEndian(extension[(i + 1)..]);
            }

            i += 1 + length;
        }

        return null;
    }
}

/// <summary>The parsed RTP header fields (RFC 3550 §5.1) plus the abs-capture-time, if present.</summary>
/// <param name="Marker">The marker bit.</param>
/// <param name="PayloadType">The payload type (7 bits).</param>
/// <param name="SequenceNumber">The RTP sequence number.</param>
/// <param name="Timestamp">The RTP timestamp.</param>
/// <param name="Ssrc">The synchronization source identifier.</param>
/// <param name="AbsoluteCaptureTimeNtp">The abs-capture-time as a UQ32.32 NTP timestamp, if the extension was present.</param>
public readonly record struct RtpHeader(
    bool Marker,
    byte PayloadType,
    ushort SequenceNumber,
    uint Timestamp,
    uint Ssrc,
    ulong? AbsoluteCaptureTimeNtp);
