using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Agash.StreamTransport.WebRtc.Srtp;

/// <summary>
/// The AES-GCM SRTP/SRTCP transform (RFC 7714), the <c>SRTP_AEAD_AES_128_GCM</c> and <c>_256_GCM</c>
/// profiles. One instance protects or unprotects one direction with that direction's session keys. The
/// payload (or RTCP body) is encrypted in place and the 16-octet tag appended; the RTP header (or RTCP
/// header and index) is the additional authenticated data, passed as a span of the packet itself.
/// </summary>
/// <remarks>
/// The keyed <see cref="AesGcm"/> contexts live as long as the direction, so a packet costs no
/// allocation. RTP and RTCP each have their own context and lock, because media and feedback are sent
/// from different loops.
/// </remarks>
internal sealed class SrtpGcmTransform : ISrtpTransform, IDisposable
{
    /// <summary>The GCM authentication tag length for SRTP (RFC 7714 section 13).</summary>
    public const int TagLength = 16;

    /// <summary>The SRTP/SRTCP session salt length (96 bits).</summary>
    public const int SaltLength = 12;

    /// <summary>The bytes SRTCP protection appends: the 4-octet E flag and index, then the tag.</summary>
    public const int RtcpTrailerLength = 4 + TagLength;

    private readonly Lock _rtpGate = new();
    private readonly Lock _rtcpGate = new();
    private readonly AesGcm _rtp;
    private readonly AesGcm _rtcp;
    private readonly byte[] _rtpSalt;
    private readonly byte[] _rtcpSalt;
    private bool _disposed;

    /// <summary>A transform from session keys directly, as the RFC 7714 test vectors give them.</summary>
    internal SrtpGcmTransform(byte[] rtpKey, byte[] rtpSalt, byte[] rtcpKey, byte[] rtcpSalt)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(rtpSalt.Length, SaltLength);
        ArgumentOutOfRangeException.ThrowIfNotEqual(rtcpSalt.Length, SaltLength);
        _rtp = new AesGcm(rtpKey, TagLength);
        _rtcp = new AesGcm(rtcpKey, TagLength);
        _rtpSalt = rtpSalt;
        _rtcpSalt = rtcpSalt;
    }

    /// <inheritdoc/>
    public int RtpOverhead => TagLength;

    /// <inheritdoc/>
    public int RtcpOverhead => RtcpTrailerLength;

    /// <summary>A transform from one direction's master key and salt (RFC 3711 key derivation).</summary>
    /// <param name="masterKey">The 128- or 256-bit master key.</param>
    /// <param name="masterSalt">The 96-bit master salt.</param>
    public static SrtpGcmTransform FromMaster(
        ReadOnlySpan<byte> masterKey,
        ReadOnlySpan<byte> masterSalt
    )
    {
        int keyLength = masterKey.Length;
        return new SrtpGcmTransform(
            SrtpKeyDerivation.Derive(
                masterKey,
                masterSalt,
                SrtpKeyDerivation.LabelRtpEncryption,
                keyLength
            ),
            SrtpKeyDerivation.Derive(
                masterKey,
                masterSalt,
                SrtpKeyDerivation.LabelRtpSalt,
                SaltLength
            ),
            SrtpKeyDerivation.Derive(
                masterKey,
                masterSalt,
                SrtpKeyDerivation.LabelRtcpEncryption,
                keyLength
            ),
            SrtpKeyDerivation.Derive(
                masterKey,
                masterSalt,
                SrtpKeyDerivation.LabelRtcpSalt,
                SaltLength
            )
        );
    }

    /// <inheritdoc/>
    public int ProtectRtp(uint rolloverCounter, Span<byte> packet, int length)
    {
        int headerLength = RtpHeaderLength(packet[..length]);
        Span<byte> iv = stackalloc byte[SaltLength];
        FormRtpIv(
            _rtpSalt,
            BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(8, 4)),
            rolloverCounter,
            BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2)),
            iv
        );

        Span<byte> payload = packet[headerLength..length];
        lock (_rtpGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _rtp.Encrypt(
                iv,
                payload,
                payload,
                packet.Slice(length, TagLength),
                packet[..headerLength]
            );
        }

        return length + TagLength;
    }

    /// <inheritdoc/>
    public bool UnprotectRtp(
        uint rolloverCounter,
        Span<byte> packet,
        int length,
        out int plaintextLength
    )
    {
        plaintextLength = 0;
        if (length < 12 + TagLength)
        {
            return false;
        }

        int headerLength = RtpHeaderLength(packet[..length]);
        int encryptedLength = length - TagLength - headerLength;
        if (encryptedLength < 0)
        {
            return false;
        }

        Span<byte> iv = stackalloc byte[SaltLength];
        FormRtpIv(
            _rtpSalt,
            BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(8, 4)),
            rolloverCounter,
            BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2)),
            iv
        );

        Span<byte> cipher = packet.Slice(headerLength, encryptedLength);
        try
        {
            lock (_rtpGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _rtp.Decrypt(
                    iv,
                    cipher,
                    packet.Slice(headerLength + encryptedLength, TagLength),
                    cipher,
                    packet[..headerLength]
                );
            }
        }
        catch (AuthenticationTagMismatchException)
        {
            // Deliberately not logged: a forged or corrupted packet is reported by the false return.
            return false;
        }

        plaintextLength = headerLength + encryptedLength;
        return true;
    }

    /// <inheritdoc/>
    public int ProtectRtcp(uint srtcpIndex, Span<byte> packet, int length)
    {
        uint trailer = 0x8000_0000u | (srtcpIndex & 0x7FFF_FFFFu);
        Span<byte> iv = stackalloc byte[SaltLength];
        FormRtcpIv(_rtcpSalt, BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4, 4)), trailer, iv);

        // The AAD is the 8-octet header and the trailer; one 12-octet stack copy joins them.
        Span<byte> aad = stackalloc byte[12];
        packet[..8].CopyTo(aad);
        BinaryPrimitives.WriteUInt32BigEndian(aad[8..], trailer);
        BinaryPrimitives.WriteUInt32BigEndian(packet[length..], trailer);

        Span<byte> body = packet[8..length];
        lock (_rtcpGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _rtcp.Encrypt(iv, body, body, packet.Slice(length + 4, TagLength), aad);
        }

        return length + RtcpTrailerLength;
    }

    /// <inheritdoc/>
    public bool UnprotectRtcp(Span<byte> packet, int length, out int plaintextLength)
    {
        plaintextLength = 0;
        if (length < 8 + RtcpTrailerLength)
        {
            return false;
        }

        int rtcpLength = length - RtcpTrailerLength;
        uint trailer = BinaryPrimitives.ReadUInt32BigEndian(packet[rtcpLength..]);
        Span<byte> iv = stackalloc byte[SaltLength];
        FormRtcpIv(_rtcpSalt, BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4, 4)), trailer, iv);

        Span<byte> aad = stackalloc byte[12];
        packet[..8].CopyTo(aad);
        BinaryPrimitives.WriteUInt32BigEndian(aad[8..], trailer);

        Span<byte> cipher = packet[8..rtcpLength];
        ReadOnlySpan<byte> tag = packet.Slice(rtcpLength + 4, TagLength);
        try
        {
            lock (_rtcpGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if ((trailer & 0x8000_0000u) != 0)
                {
                    _rtcp.Decrypt(iv, cipher, tag, cipher, aad);
                }
                else
                {
                    // E clear: the body travelled in the clear and is authenticated as AAD (RFC 7714
                    // section 9.3), so the whole packet up to the trailer is the AAD of an empty message.
                    Span<byte> clearAad = packet[..(rtcpLength + 4)];
                    _rtcp.Decrypt(iv, [], tag, [], clearAad);
                }
            }
        }
        catch (AuthenticationTagMismatchException)
        {
            // Deliberately not logged: a forged or corrupted packet is reported by the false return.
            return false;
        }

        plaintextLength = rtcpLength;
        return true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_rtpGate)
        lock (_rtcpGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _rtp.Dispose();
            _rtcp.Dispose();
        }
    }

    /// <summary>
    /// The 12-octet SRTCP IV (RFC 7714 section 9.1): <c>(00 00 || SSRC || 00 00 || index) XOR salt</c>,
    /// the E flag masked off.
    /// </summary>
    internal static void FormRtcpIv(ReadOnlySpan<byte> salt, uint ssrc, uint srtcpIndex, Span<byte> iv)
    {
        iv.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(iv[2..], ssrc);
        BinaryPrimitives.WriteUInt32BigEndian(iv[8..], srtcpIndex & 0x7FFF_FFFFu);
        Xor(iv, salt);
    }

    /// <summary>
    /// The 12-octet SRTP IV (RFC 7714 section 8.1): <c>(00 00 || SSRC || ROC || SEQ) XOR salt</c>.
    /// </summary>
    internal static void FormRtpIv(
        ReadOnlySpan<byte> salt,
        uint ssrc,
        uint roc,
        ushort seq,
        Span<byte> iv
    )
    {
        iv.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(iv[2..], ssrc);
        BinaryPrimitives.WriteUInt32BigEndian(iv[6..], roc);
        BinaryPrimitives.WriteUInt16BigEndian(iv[10..], seq);
        Xor(iv, salt);
    }

    /// <summary>The RTP header length: 12 octets, the CSRCs and the header extension.</summary>
    internal static int RtpHeaderLength(ReadOnlySpan<byte> packet)
    {
        int length = 12 + ((packet[0] & 0x0F) * 4);
        if ((packet[0] & 0x10) != 0 && length + 4 <= packet.Length)
        {
            length += 4 + (BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(length + 2, 2)) * 4);
        }

        return length;
    }

    private static void Xor(Span<byte> target, ReadOnlySpan<byte> mask)
    {
        for (int i = 0; i < target.Length; i++)
        {
            target[i] ^= mask[i];
        }
    }
}
