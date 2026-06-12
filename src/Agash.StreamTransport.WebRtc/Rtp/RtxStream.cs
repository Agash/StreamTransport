using System.Buffers.Binary;

namespace Agash.StreamTransport.WebRtc.Rtp;

/// <summary>
/// RTP retransmission (RTX) repacketization (RFC 4588): a retransmitted packet is sent on a separate RTX
/// SSRC and payload type, with its own monotonic sequence number, and its payload is the original
/// sequence number (OSN, 2 octets) followed by the original RTP payload. The receiver reverses this to
/// recover the original packet for the depacketizer.
/// </summary>
public static class RtxStream
{
    /// <summary>
    /// Wraps an original RTP packet as an RTX packet into <paramref name="destination"/>, preserving the
    /// original timestamp and marker. Returns the bytes written.
    /// </summary>
    public static bool TryWrap(ReadOnlySpan<byte> originalRtp, Span<byte> destination, byte rtxPayloadType, uint rtxSsrc, ushort rtxSequence, out int length)
    {
        length = 0;
        if (!RtpPacket.TryParse(originalRtp, out RtpHeader header, out ReadOnlySpan<byte> originalPayload))
        {
            return false;
        }

        // RTX payload = OSN (original sequence number) || original payload.
        Span<byte> rtxPayload = originalPayload.Length + 2 <= 2048 ? stackalloc byte[originalPayload.Length + 2] : new byte[originalPayload.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(rtxPayload, header.SequenceNumber);
        originalPayload.CopyTo(rtxPayload[2..]);

        length = RtpPacket.Write(destination, header.Marker, rtxPayloadType, rtxSequence, header.Timestamp, rtxSsrc, rtxPayload);
        return true;
    }

    /// <summary>
    /// Reverses <see cref="TryWrap"/>: recovers the original RTP packet from an RTX packet into
    /// <paramref name="destination"/>, restoring the original payload type and SSRC.
    /// </summary>
    public static bool TryUnwrap(ReadOnlySpan<byte> rtxRtp, Span<byte> destination, byte originalPayloadType, uint originalSsrc, out int length)
    {
        length = 0;
        if (!RtpPacket.TryParse(rtxRtp, out RtpHeader header, out ReadOnlySpan<byte> rtxPayload) || rtxPayload.Length < 2)
        {
            return false;
        }

        ushort originalSequence = BinaryPrimitives.ReadUInt16BigEndian(rtxPayload);
        length = RtpPacket.Write(destination, header.Marker, originalPayloadType, originalSequence, header.Timestamp, originalSsrc, rtxPayload[2..]);
        return true;
    }
}
