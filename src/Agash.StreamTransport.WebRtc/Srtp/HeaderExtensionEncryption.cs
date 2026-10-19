using System.Buffers.Binary;
using System.Numerics.Tensors;
using System.Security.Cryptography;
using Agash.StreamTransport.WebRtc.Rtp;

namespace Agash.StreamTransport.WebRtc.Srtp;

/// <summary>
/// Encryption of chosen RTP header extension elements (RFC 6904), which WebRTC endpoints are required to
/// be capable of (RFC 8834 section 5.2.2). The header keys come from the master key with labels 0x06 and
/// 0x07 (section 3); the keystream is AES counter mode with the packet's SRTP IV under those keys (section
/// 3.2), for the AES-GCM profiles too (RFC 7714 section 8.3), and starts at the first element. Only the
/// payloads of the chosen elements are XORed with it: element headers and padding stay as they are.
/// </summary>
internal sealed class HeaderExtensionEncryption : IDisposable
{
    private const int SaltLength = 14;
    private const ushort OneByteProfile = 0xBEDE;

    private readonly Aes _cipher;
    private readonly byte[] _salt;
    private byte[] _keystream = [];

    private HeaderExtensionEncryption(byte[] key, byte[] salt)
    {
        _cipher = Aes.Create();
        _cipher.Key = key;
        _salt = salt;
    }

    /// <summary>The header keys of one direction.</summary>
    /// <param name="masterKey">The master key.</param>
    /// <param name="masterSalt">The master salt.</param>
    /// <param name="saltLength">
    /// The session salt length of the profile: 14 for AES-CM; 12 for AES-GCM, zero-padded to the 14 counter
    /// mode takes, as libsrtp does.
    /// </param>
    /// <returns>The keys.</returns>
    public static HeaderExtensionEncryption FromMaster(
        ReadOnlySpan<byte> masterKey,
        ReadOnlySpan<byte> masterSalt,
        int saltLength
    )
    {
        byte[] salt = new byte[SaltLength];
        SrtpKeyDerivation
            .Derive(masterKey, masterSalt, SrtpKeyDerivation.LabelRtpHeaderSalt, saltLength)
            .CopyTo(salt, 0);
        return new HeaderExtensionEncryption(
            SrtpKeyDerivation.Derive(
                masterKey,
                masterSalt,
                SrtpKeyDerivation.LabelRtpHeaderEncryption,
                masterKey.Length
            ),
            salt
        );
    }

    /// <summary>
    /// Encrypts or decrypts, in place, the payloads of the packet's extension elements whose identifiers are
    /// in the set. A sender calls it before the payload is protected; a receiver after the packet
    /// authenticated (section 3).
    /// </summary>
    /// <param name="packet">The RTP packet.</param>
    /// <param name="length">Its length, without SRTP's trailer.</param>
    /// <param name="rolloverCounter">The packet's rollover counter.</param>
    /// <param name="encrypted">The identifiers whose elements are encrypted.</param>
    public void Apply(
        Span<byte> packet,
        int length,
        uint rolloverCounter,
        RtpExtensionIdSet encrypted
    )
    {
        if (encrypted.IsEmpty || (packet[0] & 0x10) == 0)
        {
            return;
        }

        int at = 12 + ((packet[0] & 0x0F) * 4);
        if (at + 4 > length)
        {
            return;
        }

        ushort profile = BinaryPrimitives.ReadUInt16BigEndian(packet[at..]);
        bool twoByte = (profile & 0xFFF0) == 0x1000;
        if (!twoByte && profile != OneByteProfile)
        {
            return;
        }

        int end = Math.Min(
            length,
            at + 4 + (BinaryPrimitives.ReadUInt16BigEndian(packet[(at + 2)..]) * 4)
        );
        Span<byte> block = packet[(at + 4)..end];
        ReadOnlySpan<byte> keystream = Keystream(
            BinaryPrimitives.ReadUInt32BigEndian(packet[8..]),
            ((ulong)rolloverCounter << 16) | BinaryPrimitives.ReadUInt16BigEndian(packet[2..]),
            block.Length
        );

        int i = 0;
        while (i < block.Length)
        {
            if (block[i] == 0)
            {
                i++;
                continue;
            }

            int id;
            int size;
            if (twoByte)
            {
                if (i + 2 > block.Length)
                {
                    return;
                }

                id = block[i];
                size = block[i + 1];
                i += 2;
            }
            else
            {
                id = block[i] >> 4;
                size = (block[i] & 0x0F) + 1;
                i++;
                if (id == 15)
                {
                    return;
                }
            }

            if (i + size > block.Length)
            {
                return;
            }

            if (encrypted.Contains(id))
            {
                Span<byte> payload = block.Slice(i, size);
                TensorPrimitives.Xor(payload, keystream.Slice(i, size), payload);
            }

            i += size;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _cipher.Dispose();

    // RFC 3711 section 4.1.1: AES counter mode from the packet's IV, as long as the block.
    private ReadOnlySpan<byte> Keystream(uint ssrc, ulong index, int length)
    {
        int needed = (length + 15) / 16 * 16;
        if (_keystream.Length < needed)
        {
            _keystream = new byte[needed];
        }

        Span<byte> counters = _keystream.AsSpan(0, needed);
        Span<byte> iv = stackalloc byte[16];
        SrtpCmTransform.Keystream.InitialCounter(_salt, ssrc, index, iv);
        for (int block = 0; block < needed / 16; block++)
        {
            Span<byte> counter = counters.Slice(block * 16, 16);
            iv.CopyTo(counter);
            BinaryPrimitives.WriteUInt16BigEndian(
                counter[14..],
                (ushort)(BinaryPrimitives.ReadUInt16BigEndian(iv[14..]) + block)
            );
        }

        _ = _cipher.EncryptEcb(counters, counters, PaddingMode.None);
        return counters[..length];
    }
}
