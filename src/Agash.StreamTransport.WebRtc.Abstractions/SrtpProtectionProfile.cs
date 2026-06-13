namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// SRTP protection profiles negotiated by the DTLS-SRTP <c>use_srtp</c> extension (RFC 5764 §4.1.2),
/// identified by their IANA "SRTP Protection Profile" code points. The value of each member is that
/// two-byte code point as it appears on the wire.
/// </summary>
/// <remarks>
/// AEAD (AES-GCM, RFC 7714) profiles are preferred over the legacy AES-CTR + HMAC-SHA1 profiles. The
/// PERC "double" profiles (RFC 8723) are intentionally not modelled: this transport is point-to-point,
/// and a double transform corrupts RTP header extensions.
/// </remarks>
public enum SrtpProtectionProfile : ushort
{
    /// <summary>No profile selected / negotiation has not completed.</summary>
    None = 0x0000,

    /// <summary><c>SRTP_AES128_CM_HMAC_SHA1_80</c> - AES-128 counter mode, 80-bit HMAC-SHA1 auth tag.</summary>
    Aes128CmHmacSha1_80 = 0x0001,

    /// <summary><c>SRTP_AES128_CM_HMAC_SHA1_32</c> - AES-128 counter mode, 32-bit HMAC-SHA1 auth tag.</summary>
    Aes128CmHmacSha1_32 = 0x0002,

    /// <summary><c>SRTP_AEAD_AES_128_GCM</c> - AES-128 in Galois/Counter Mode (RFC 7714).</summary>
    AeadAes128Gcm = 0x0007,

    /// <summary><c>SRTP_AEAD_AES_256_GCM</c> - AES-256 in Galois/Counter Mode (RFC 7714).</summary>
    AeadAes256Gcm = 0x0008,
}
