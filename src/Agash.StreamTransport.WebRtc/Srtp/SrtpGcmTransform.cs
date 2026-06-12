using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Agash.StreamTransport.WebRtc.Srtp;

/// <summary>
/// The AES-GCM SRTP/SRTCP transform (RFC 7714). Encrypts the RTP payload (or RTCP body) in place and
/// appends the 16-octet GCM authentication tag, using the RTP header (or RTCP header + index) as
/// additional authenticated data. Operates on a single session key + salt; key derivation and rollover
/// tracking live in the SRTP session that drives this transform.
/// </summary>
/// <remarks>
/// AEAD (GCM) only — the legacy AES-CTR + HMAC-SHA1 suite (RFC 3711) is not implemented, because both
/// peers are our own first-party stack and negotiate a GCM profile. The tag is always 16 octets
/// (<c>AEAD_AES_128_GCM</c> / <c>AEAD_AES_256_GCM</c>).
/// </remarks>
public static class SrtpGcmTransform
{
    /// <summary>The GCM authentication tag length for SRTP (RFC 7714 §13).</summary>
    public const int TagLength = 16;

    /// <summary>The SRTP/SRTCP session salt length (96 bits).</summary>
    public const int SaltLength = 12;

    /// <summary>
    /// Encrypts an RTP packet in place (RFC 7714 §8/§12): the header is left clear and authenticated, the
    /// payload is encrypted, and the 16-octet tag is appended. <paramref name="packet"/> must have room for
    /// <paramref name="length"/> + <see cref="TagLength"/> bytes.
    /// </summary>
    public static int ProtectRtp(ReadOnlySpan<byte> sessionKey, ReadOnlySpan<byte> sessionSalt, uint rolloverCounter, Span<byte> packet, int length)
    {
        int headerLength = RtpHeaderLength(packet[..length]);
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(8, 4));
        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));

        Span<byte> iv = stackalloc byte[SaltLength];
        FormRtpIv(sessionSalt, ssrc, rolloverCounter, seq, iv);

        Span<byte> payload = packet[headerLength..length];
        Span<byte> tag = packet.Slice(length, TagLength);
        using var gcm = new AesGcm(sessionKey, TagLength);
        gcm.Encrypt(iv, payload, payload, tag, packet[..headerLength]);
        return length + TagLength;
    }

    /// <summary>
    /// Decrypts and authenticates an RTP packet produced by <see cref="ProtectRtp"/>, writing the
    /// recovered plaintext length to <paramref name="plaintextLength"/>. Returns <see langword="false"/>
    /// (without modifying the caller's view of validity) if authentication fails.
    /// </summary>
    public static bool UnprotectRtp(ReadOnlySpan<byte> sessionKey, ReadOnlySpan<byte> sessionSalt, uint rolloverCounter, Span<byte> packet, int length, out int plaintextLength)
    {
        plaintextLength = 0;
        if (length < TagLength)
        {
            return false;
        }

        int headerLength = RtpHeaderLength(packet[..length]);
        int encryptedLength = length - TagLength - headerLength;
        if (encryptedLength < 0)
        {
            return false;
        }

        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(8, 4));
        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));

        Span<byte> iv = stackalloc byte[SaltLength];
        FormRtpIv(sessionSalt, ssrc, rolloverCounter, seq, iv);

        Span<byte> cipher = packet.Slice(headerLength, encryptedLength);
        ReadOnlySpan<byte> tag = packet.Slice(headerLength + encryptedLength, TagLength);
        try
        {
            using var gcm = new AesGcm(sessionKey, TagLength);
            gcm.Decrypt(iv, cipher, tag, cipher, packet[..headerLength]);
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }

        plaintextLength = headerLength + encryptedLength;
        return true;
    }

    /// <summary>The bytes SRTCP protection appends: the 4-octet E-flag/index trailer plus the GCM tag.</summary>
    public const int RtcpOverhead = 4 + TagLength;

    /// <summary>
    /// Encrypts an RTCP packet in place (RFC 7714 §9): the 8-octet header is authenticated, the rest is
    /// encrypted, then the 4-octet E-flag/SRTCP-index trailer and the 16-octet tag are appended.
    /// </summary>
    public static int ProtectRtcp(ReadOnlySpan<byte> sessionKey, ReadOnlySpan<byte> sessionSalt, uint srtcpIndex, Span<byte> packet, int length)
    {
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4, 4));

        Span<byte> aad = stackalloc byte[12];
        packet[..8].CopyTo(aad);
        uint trailer = 0x8000_0000u | (srtcpIndex & 0x7FFF_FFFFu); // E=1
        BinaryPrimitives.WriteUInt32BigEndian(aad[8..], trailer);

        Span<byte> iv = stackalloc byte[SaltLength];
        FormRtcpIv(sessionSalt, ssrc, srtcpIndex, iv);

        Span<byte> payload = packet[8..length];
        BinaryPrimitives.WriteUInt32BigEndian(packet[length..], trailer);
        Span<byte> tag = packet.Slice(length + 4, TagLength);
        using var gcm = new AesGcm(sessionKey, TagLength);
        gcm.Encrypt(iv, payload, payload, tag, aad);
        return length + RtcpOverhead;
    }

    /// <summary>
    /// Authenticates and decrypts an SRTCP packet produced by <see cref="ProtectRtcp"/>, writing the
    /// recovered RTCP length to <paramref name="plaintextLength"/>. Returns <see langword="false"/> on
    /// authentication failure.
    /// </summary>
    public static bool UnprotectRtcp(ReadOnlySpan<byte> sessionKey, ReadOnlySpan<byte> sessionSalt, Span<byte> packet, int length, out int plaintextLength)
    {
        plaintextLength = 0;
        if (length < 8 + RtcpOverhead)
        {
            return false;
        }

        int rtcpLength = length - RtcpOverhead;
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4, 4));
        uint trailer = BinaryPrimitives.ReadUInt32BigEndian(packet[rtcpLength..]);
        uint index = trailer & 0x7FFF_FFFFu;

        Span<byte> aad = stackalloc byte[12];
        packet[..8].CopyTo(aad);
        BinaryPrimitives.WriteUInt32BigEndian(aad[8..], trailer);

        Span<byte> iv = stackalloc byte[SaltLength];
        FormRtcpIv(sessionSalt, ssrc, index, iv);

        Span<byte> cipher = packet[8..rtcpLength];
        ReadOnlySpan<byte> tag = packet.Slice(rtcpLength + 4, TagLength);
        try
        {
            using var gcm = new AesGcm(sessionKey, TagLength);
            gcm.Decrypt(iv, cipher, tag, cipher, aad);
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }

        plaintextLength = rtcpLength;
        return true;
    }

    /// <summary>Forms the 12-octet AES-GCM SRTCP IV (RFC 7714 §9.1): <c>(00 00 || SSRC || 00 00 || index) XOR salt</c>.</summary>
    internal static void FormRtcpIv(ReadOnlySpan<byte> salt, uint ssrc, uint srtcpIndex, Span<byte> iv)
    {
        Span<byte> x = stackalloc byte[SaltLength];
        BinaryPrimitives.WriteUInt32BigEndian(x[2..], ssrc);
        BinaryPrimitives.WriteUInt32BigEndian(x[8..], srtcpIndex & 0x7FFF_FFFFu);
        for (int i = 0; i < SaltLength; i++)
        {
            iv[i] = (byte)(salt[i] ^ x[i]);
        }
    }

    /// <summary>
    /// Forms the 12-octet AES-GCM SRTP IV (RFC 7714 §8.1): <c>(00 00 || SSRC || ROC || SEQ) XOR salt</c>.
    /// </summary>
    internal static void FormRtpIv(ReadOnlySpan<byte> salt, uint ssrc, uint roc, ushort seq, Span<byte> iv)
    {
        Span<byte> x = stackalloc byte[SaltLength];
        x[0] = 0;
        x[1] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(x[2..], ssrc);
        BinaryPrimitives.WriteUInt32BigEndian(x[6..], roc);
        BinaryPrimitives.WriteUInt16BigEndian(x[10..], seq);
        for (int i = 0; i < SaltLength; i++)
        {
            iv[i] = (byte)(salt[i] ^ x[i]);
        }
    }

    /// <summary>Computes the RTP header length (12 + 4·CC + extension), accounting for CSRCs and one extension.</summary>
    internal static int RtpHeaderLength(ReadOnlySpan<byte> packet)
    {
        int csrcCount = packet[0] & 0x0F;
        int length = 12 + (csrcCount * 4);
        bool hasExtension = (packet[0] & 0x10) != 0;
        if (hasExtension && length + 4 <= packet.Length)
        {
            int words = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(length + 2, 2));
            length += 4 + (words * 4);
        }

        return length;
    }
}
