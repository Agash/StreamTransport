using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Agash.StreamTransport.WebRtc.Srtp;

/// <summary>
/// The AES counter-mode SRTP/SRTCP transform with HMAC-SHA1 authentication (RFC 3711), the
/// <c>SRTP_AES128_CM_HMAC_SHA1_80</c> profile (RFC 5764). It serves peers that negotiate no AES-GCM
/// profile (older libsrtp builds, some WHIP/WHEP clients). One instance protects or unprotects one
/// direction with that direction's session keys.
/// </summary>
/// <remarks>
/// RTP: the payload is encrypted with the AES-CM keystream, and an HMAC-SHA1 tag over the header, the
/// encrypted payload and the rollover counter is appended, truncated to 80 bits. SRTCP: everything after
/// the first 8 octets is encrypted, the E flag and SRTCP index follow, then the same 80-bit tag. The
/// keystream buffer, ciphers and MACs are reused, so a packet costs no allocation.
/// </remarks>
internal sealed class SrtpCmTransform : ISrtpTransform, IDisposable
{
    /// <summary>The AES-CM session salt length (112 bits).</summary>
    public const int SaltLength = 14;

    /// <summary>The HMAC-SHA1 session authentication key length (160 bits).</summary>
    public const int AuthKeyLength = 20;

    /// <summary>The truncated HMAC-SHA1 tag length (80 bits) for both SRTP and SRTCP.</summary>
    public const int TagLength = 10;

    private const int IndexLength = 4;

    private readonly Lock _gate = new();
    private readonly Aes _rtpCipher;
    private readonly Aes _rtcpCipher;
    private readonly byte[] _rtpSalt;
    private readonly byte[] _rtcpSalt;
    private readonly IncrementalHash _rtpMac;
    private readonly IncrementalHash _rtcpMac;
    private byte[] _keystream = new byte[1536];
    private bool _disposed;

    /// <summary>A transform from one direction's master key and salt.</summary>
    /// <param name="masterKey">The 128-bit master key.</param>
    /// <param name="masterSalt">The 112-bit master salt.</param>
    public SrtpCmTransform(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> masterSalt)
        : this(
            SrtpKeyDerivation.Derive(
                masterKey,
                masterSalt,
                SrtpKeyDerivation.LabelRtpEncryption,
                16
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
                SrtpKeyDerivation.LabelRtpAuthentication,
                AuthKeyLength
            ),
            SrtpKeyDerivation.Derive(
                masterKey,
                masterSalt,
                SrtpKeyDerivation.LabelRtcpEncryption,
                16
            ),
            SrtpKeyDerivation.Derive(
                masterKey,
                masterSalt,
                SrtpKeyDerivation.LabelRtcpSalt,
                SaltLength
            ),
            SrtpKeyDerivation.Derive(
                masterKey,
                masterSalt,
                SrtpKeyDerivation.LabelRtcpAuthentication,
                AuthKeyLength
            )
        ) { }

    /// <summary>A transform from session keys directly, as the RFC 3711 test vectors give them.</summary>
    internal SrtpCmTransform(
        byte[] rtpKey,
        byte[] rtpSalt,
        byte[] rtpAuthKey,
        byte[] rtcpKey,
        byte[] rtcpSalt,
        byte[] rtcpAuthKey
    )
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(rtpSalt.Length, SaltLength);
        ArgumentOutOfRangeException.ThrowIfNotEqual(rtcpSalt.Length, SaltLength);
        _rtpCipher = Cipher(rtpKey);
        _rtcpCipher = Cipher(rtcpKey);
        _rtpSalt = rtpSalt;
        _rtcpSalt = rtcpSalt;
        _rtpMac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, rtpAuthKey);
        _rtcpMac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, rtcpAuthKey);
    }

    /// <inheritdoc/>
    public int RtpOverhead => TagLength;

    /// <inheritdoc/>
    public int RtcpOverhead => IndexLength + TagLength;

    /// <inheritdoc/>
    public int ProtectRtp(uint rolloverCounter, Span<byte> packet, int length)
    {
        int header = SrtpGcmTransform.RtpHeaderLength(packet[..length]);
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(8, 4));
        ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Crypt(
                _rtpCipher,
                _rtpSalt,
                ssrc,
                ((ulong)rolloverCounter << 16) | sequence,
                packet[header..length]
            );
            Tag(_rtpMac, packet[..length], rolloverCounter, packet.Slice(length, RtpOverhead));
        }

        return length + RtpOverhead;
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
        int authenticated = length - RtpOverhead;
        if (authenticated < 12)
        {
            return false;
        }

        int header = SrtpGcmTransform.RtpHeaderLength(packet[..authenticated]);
        if (header > authenticated)
        {
            return false;
        }

        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(8, 4));
        ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        Span<byte> expected = stackalloc byte[RtpOverhead];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Tag(_rtpMac, packet[..authenticated], rolloverCounter, expected);
            if (
                !CryptographicOperations.FixedTimeEquals(
                    expected,
                    packet.Slice(authenticated, RtpOverhead)
                )
            )
            {
                return false;
            }

            Crypt(
                _rtpCipher,
                _rtpSalt,
                ssrc,
                ((ulong)rolloverCounter << 16) | sequence,
                packet[header..authenticated]
            );
        }

        plaintextLength = authenticated;
        return true;
    }

    /// <inheritdoc/>
    public int ProtectRtcp(uint srtcpIndex, Span<byte> packet, int length)
    {
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4, 4));
        uint trailer = 0x8000_0000u | (srtcpIndex & 0x7FFF_FFFFu);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Crypt(_rtcpCipher, _rtcpSalt, ssrc, srtcpIndex & 0x7FFF_FFFFu, packet[8..length]);
            BinaryPrimitives.WriteUInt32BigEndian(packet[length..], trailer);
            _rtcpMac.AppendData(packet[..(length + IndexLength)]);
            Span<byte> mac = stackalloc byte[20];
            _ = _rtcpMac.GetHashAndReset(mac);
            mac[..TagLength].CopyTo(packet[(length + IndexLength)..]);
        }

        return length + IndexLength + TagLength;
    }

    /// <inheritdoc/>
    public bool UnprotectRtcp(Span<byte> packet, int length, out int plaintextLength)
    {
        plaintextLength = 0;
        int authenticated = length - TagLength;
        int rtcpLength = authenticated - IndexLength;
        if (rtcpLength < 8)
        {
            return false;
        }

        uint trailer = BinaryPrimitives.ReadUInt32BigEndian(packet[rtcpLength..]);
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4, 4));
        Span<byte> mac = stackalloc byte[20];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _rtcpMac.AppendData(packet[..authenticated]);
            _ = _rtcpMac.GetHashAndReset(mac);
            if (
                !CryptographicOperations.FixedTimeEquals(
                    mac[..TagLength],
                    packet.Slice(authenticated, TagLength)
                )
            )
            {
                return false;
            }

            // The E flag clear means the body travelled unencrypted.
            if ((trailer & 0x8000_0000u) != 0)
            {
                Crypt(_rtcpCipher, _rtcpSalt, ssrc, trailer & 0x7FFF_FFFFu, packet[8..rtcpLength]);
            }
        }

        plaintextLength = rtcpLength;
        return true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _rtpCipher.Dispose();
            _rtcpCipher.Dispose();
            _rtpMac.Dispose();
            _rtcpMac.Dispose();
        }
    }

    // XORs the AES-CM keystream for a packet into data: counter blocks start from
    // IV = (salt * 2^16) XOR (SSRC * 2^64) XOR (index * 2^16), RFC 3711 section 4.1.1. Caller holds _gate.
    private void Crypt(Aes cipher, byte[] salt, uint ssrc, ulong index, Span<byte> data)
    {
        int blocks = (data.Length + 15) / 16;
        if (blocks == 0)
        {
            return;
        }

        int needed = blocks * 16;
        if (_keystream.Length < needed)
        {
            _keystream = new byte[needed];
        }

        Span<byte> counters = _keystream.AsSpan(0, needed);
        Span<byte> iv = stackalloc byte[16];
        Keystream.InitialCounter(salt, ssrc, index, iv);
        for (int block = 0; block < blocks; block++)
        {
            Span<byte> counter = counters.Slice(block * 16, 16);
            iv.CopyTo(counter);
            ushort low = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(iv[14..]) + block);
            BinaryPrimitives.WriteUInt16BigEndian(counter[14..], low);
        }

        _ = cipher.EncryptEcb(counters, counters, PaddingMode.None);
        for (int i = 0; i < data.Length; i++)
        {
            data[i] ^= counters[i];
        }
    }

    // HMAC-SHA1 over the authenticated portion and the rollover counter, truncated to the tag.
    private static void Tag(
        IncrementalHash mac,
        ReadOnlySpan<byte> authenticated,
        uint rolloverCounter,
        Span<byte> tag
    )
    {
        Span<byte> roc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(roc, rolloverCounter);
        mac.AppendData(authenticated);
        mac.AppendData(roc);
        Span<byte> full = stackalloc byte[20];
        _ = mac.GetHashAndReset(full);
        full[..tag.Length].CopyTo(tag);
    }

    private static Aes Cipher(byte[] key)
    {
        var aes = Aes.Create();
        aes.Key = key;
        return aes;
    }

    /// <summary>The AES-CM counter layout, shared with the tests that check it against RFC 3711.</summary>
    internal static class Keystream
    {
        /// <summary>The first counter block for a packet.</summary>
        public static void InitialCounter(
            ReadOnlySpan<byte> salt,
            uint ssrc,
            ulong index,
            Span<byte> counter
        )
        {
            counter.Clear();
            salt.CopyTo(counter);
            Span<byte> ssrcBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(ssrcBytes, ssrc);
            for (int i = 0; i < 4; i++)
            {
                counter[4 + i] ^= ssrcBytes[i];
            }

            for (int i = 0; i < 6; i++)
            {
                counter[8 + i] ^= (byte)(index >> (8 * (5 - i)));
            }
        }
    }
}
