using Agash.StreamTransport.WebRtc.Rtp;

namespace Agash.StreamTransport.WebRtc.Srtp;

/// <summary>
/// One direction's SRTP and SRTCP protection under one protection profile, holding that direction's
/// session keys. The session picks the implementation from the profile DTLS negotiated.
/// </summary>
internal interface ISrtpTransform
{
    /// <summary>The bytes protection adds to an RTP packet.</summary>
    int RtpOverhead { get; }

    /// <summary>The bytes protection adds to an RTCP packet.</summary>
    int RtcpOverhead { get; }

    /// <summary>Protects an RTP packet in place; the buffer has <see cref="RtpOverhead"/> spare octets.</summary>
    /// <param name="rolloverCounter">The packet's rollover counter.</param>
    /// <param name="packet">The packet and room after it.</param>
    /// <param name="length">The packet's length.</param>
    /// <param name="cryptex">
    /// Whether to encrypt its header extensions too (RFC 9335), when it has any.
    /// </param>
    /// <param name="encrypted">
    /// The extension elements to encrypt one by one (RFC 6904) when the packet does not go as Cryptex.
    /// </param>
    /// <returns>The protected length.</returns>
    int ProtectRtp(
        uint rolloverCounter,
        Span<byte> packet,
        int length,
        bool cryptex,
        RtpExtensionIdSet encrypted = default
    );

    /// <summary>
    /// Authenticates and decrypts an RTP packet in place, its header extensions too when it was sent as
    /// Cryptex (RFC 9335), which leaves them in the RFC 8285 form.
    /// </summary>
    /// <param name="rolloverCounter">The estimated rollover counter.</param>
    /// <param name="packet">The protected packet.</param>
    /// <param name="length">Its length.</param>
    /// <param name="plaintextLength">The recovered packet's length.</param>
    /// <param name="encrypted">The extension elements encrypted one by one (RFC 6904).</param>
    /// <returns>False when authentication fails.</returns>
    bool UnprotectRtp(
        uint rolloverCounter,
        Span<byte> packet,
        int length,
        out int plaintextLength,
        RtpExtensionIdSet encrypted = default
    );

    /// <summary>Protects an RTCP packet in place; the buffer has <see cref="RtcpOverhead"/> spare octets.</summary>
    /// <param name="srtcpIndex">The packet's SRTCP index.</param>
    /// <param name="packet">The packet and room after it.</param>
    /// <param name="length">The packet's length.</param>
    /// <returns>The protected length.</returns>
    int ProtectRtcp(uint srtcpIndex, Span<byte> packet, int length);

    /// <summary>Authenticates and decrypts an SRTCP packet in place.</summary>
    /// <param name="packet">The protected packet.</param>
    /// <param name="length">Its length.</param>
    /// <param name="plaintextLength">The recovered packet's length.</param>
    /// <returns>False when authentication fails.</returns>
    bool UnprotectRtcp(Span<byte> packet, int length, out int plaintextLength);
}
