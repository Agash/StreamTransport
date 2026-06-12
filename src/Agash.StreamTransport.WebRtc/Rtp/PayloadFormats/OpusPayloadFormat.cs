namespace Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

/// <summary>
/// The Opus RTP payload format (RFC 7587): one Opus packet per RTP packet, the payload carried verbatim
/// with no aggregation or fragmentation. Packetization and depacketization are therefore the identity —
/// this type exists to make the audio path symmetric with the H.265 one and to document the contract.
/// </summary>
public static class OpusPayloadFormat
{
    /// <summary>The RTP clock rate Opus always uses in RTP (RFC 7587 §4.1), regardless of audio sample rate.</summary>
    public const int ClockRate = 48000;

    /// <summary>An Opus packet is its own RTP payload; this returns it unchanged.</summary>
    public static ReadOnlyMemory<byte> Packetize(ReadOnlyMemory<byte> opusPacket) => opusPacket;

    /// <summary>An RTP payload is a whole Opus packet; this returns it unchanged.</summary>
    public static ReadOnlyMemory<byte> Depacketize(ReadOnlyMemory<byte> rtpPayload) => rtpPayload;
}
