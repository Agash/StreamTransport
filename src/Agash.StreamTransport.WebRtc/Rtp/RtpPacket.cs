using System.Buffers.Binary;

namespace Agash.StreamTransport.WebRtc.Rtp;

/// <summary>
/// Reads and writes RTP packets (RFC 3550 section 5.1) with header extensions (RFC 8285) under the
/// identifiers the two sides negotiated in an <see cref="RtpExtensionMap"/>. Allocation-free: parsing
/// borrows the caller's buffer and writing composes into a caller-supplied span.
/// </summary>
public static class RtpPacket
{
    /// <summary>The fixed RTP header length (no CSRCs, no extension).</summary>
    public const int FixedHeaderLength = 12;

    /// <summary>
    /// Composes an RTP packet into <paramref name="destination"/>: the fixed header, the extension block
    /// for the <paramref name="extensions"/> the map has identifiers for, then the payload.
    /// </summary>
    /// <param name="destination">Where the packet goes.</param>
    /// <param name="marker">The marker bit.</param>
    /// <param name="payloadType">The payload type.</param>
    /// <param name="sequenceNumber">The sequence number.</param>
    /// <param name="timestamp">The RTP timestamp.</param>
    /// <param name="ssrc">The SSRC.</param>
    /// <param name="payload">The payload.</param>
    /// <param name="map">The negotiated extension identifiers; null writes no extensions.</param>
    /// <param name="extensions">The extension values.</param>
    /// <returns>The packet's length.</returns>
    public static int Write(
        Span<byte> destination,
        bool marker,
        byte payloadType,
        ushort sequenceNumber,
        uint timestamp,
        uint ssrc,
        ReadOnlySpan<byte> payload,
        RtpExtensionMap? map = null,
        RtpExtensionValues extensions = default
    ) =>
        Write(
            destination,
            marker,
            payloadType,
            sequenceNumber,
            timestamp,
            ssrc,
            payload,
            map,
            extensions,
            out _
        );

    /// <summary>
    /// Composes an RTP packet as <see cref="Write(Span{byte}, bool, byte, ushort, uint, uint, ReadOnlySpan{byte}, RtpExtensionMap?, RtpExtensionValues)"/>
    /// does, reporting where the video-timing value landed so the pacer can stamp its exit time in place.
    /// </summary>
    /// <param name="destination">Where the packet goes.</param>
    /// <param name="marker">The marker bit.</param>
    /// <param name="payloadType">The payload type.</param>
    /// <param name="sequenceNumber">The sequence number.</param>
    /// <param name="timestamp">The RTP timestamp.</param>
    /// <param name="ssrc">The SSRC.</param>
    /// <param name="payload">The payload.</param>
    /// <param name="map">The negotiated extension identifiers; null writes no extensions.</param>
    /// <param name="extensions">The extension values.</param>
    /// <param name="videoTimingAt">Where the video-timing value starts in the packet, or -1.</param>
    /// <returns>The packet's length.</returns>
    public static int Write(
        Span<byte> destination,
        bool marker,
        byte payloadType,
        ushort sequenceNumber,
        uint timestamp,
        uint ssrc,
        ReadOnlySpan<byte> payload,
        RtpExtensionMap? map,
        RtpExtensionValues extensions,
        out int videoTimingAt
    )
    {
        destination[0] = 0x80; // V=2, P=0, CC=0; X set below when there is an extension block.
        destination[1] = (byte)((marker ? 0x80 : 0) | (payloadType & 0x7F));
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], sequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], ssrc);

        int offset = FixedHeaderLength;
        videoTimingAt = -1;
        if (map is not null && !extensions.IsEmpty)
        {
            int block = map.Write(destination[offset..], extensions, out int timingAt);
            if (block > 0)
            {
                destination[0] |= 0x10; // X = 1
                videoTimingAt = timingAt < 0 ? -1 : offset + timingAt;
                offset += block;
            }
        }

        payload.CopyTo(destination[offset..]);
        return offset + payload.Length;
    }

    /// <summary>Parses an RTP packet, ignoring its header extensions.</summary>
    /// <param name="packet">The packet.</param>
    /// <param name="header">Its header fields.</param>
    /// <param name="payload">Its payload, borrowed from the packet.</param>
    /// <returns>Whether it is a well-formed RTP packet.</returns>
    public static bool TryParse(
        ReadOnlySpan<byte> packet,
        out RtpHeader header,
        out ReadOnlySpan<byte> payload
    ) => TryParse(packet, RtpExtensionMap.None, out header, out payload);

    /// <summary>
    /// Parses an RTP packet, reading the values of the header extensions the map has identifiers for.
    /// </summary>
    /// <param name="packet">The packet.</param>
    /// <param name="map">The negotiated extension identifiers.</param>
    /// <param name="header">Its header fields and extension values.</param>
    /// <param name="payload">Its payload, borrowed from the packet.</param>
    /// <returns>Whether it is a well-formed RTP packet.</returns>
    public static bool TryParse(
        ReadOnlySpan<byte> packet,
        RtpExtensionMap map,
        out RtpHeader header,
        out ReadOnlySpan<byte> payload
    )
    {
        ArgumentNullException.ThrowIfNull(map);
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

        RtpExtensionValues extensions = default;
        if (hasExtension)
        {
            if (offset + 4 > packet.Length)
            {
                return false;
            }

            ushort profile = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            int words = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            int dataStart = offset + 4;
            int dataEnd = dataStart + (words * 4);
            if (dataEnd > packet.Length)
            {
                return false;
            }

            extensions = map.Read(profile, packet[dataStart..dataEnd]);
            offset = dataEnd;
        }

        payload = packet[offset..];
        header = new RtpHeader(marker, payloadType, sequenceNumber, timestamp, ssrc, extensions);
        return true;
    }
}

/// <summary>The parsed RTP header fields (RFC 3550 section 5.1) and the extension values read with them.</summary>
/// <param name="Marker">The marker bit.</param>
/// <param name="PayloadType">The payload type (7 bits).</param>
/// <param name="SequenceNumber">The RTP sequence number.</param>
/// <param name="Timestamp">The RTP timestamp.</param>
/// <param name="Ssrc">The synchronization source identifier.</param>
/// <param name="Extensions">The values of the negotiated extensions the packet carries.</param>
public readonly record struct RtpHeader(
    bool Marker,
    byte PayloadType,
    ushort SequenceNumber,
    uint Timestamp,
    uint Ssrc,
    RtpExtensionValues Extensions = default
)
{
    /// <summary>The abs-capture-time as a UQ32.32 NTP timestamp, when the packet carries it.</summary>
    public ulong? AbsoluteCaptureTimeNtp => Extensions.AbsoluteCaptureTimeNtp;
}
