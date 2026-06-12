using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Agash.StreamTransport.WebRtc.Srtp;

/// <summary>
/// The SRTP key-derivation function (RFC 3711 §4.3, AES_CM PRF; RFC 6188 for AES-256). Derives the
/// per-session encryption key and salt from a master key + master salt, for a given key-derivation label.
/// </summary>
/// <remarks>
/// The KDF master salt is 112 bits (14 octets). AES-GCM (RFC 7714) exports a 96-bit (12-octet) master
/// salt; it is zero-padded to 14 octets here, matching libsrtp/libwebrtc. The key-derivation rate is 0
/// (single derivation per master key), as is the SRTP default.
/// </remarks>
public static class SrtpKeyDerivation
{
    /// <summary>SRTP encryption key label (RFC 3711 §4.3.2).</summary>
    public const byte LabelRtpEncryption = 0x00;

    /// <summary>SRTP salting key label.</summary>
    public const byte LabelRtpSalt = 0x02;

    /// <summary>SRTCP encryption key label.</summary>
    public const byte LabelRtcpEncryption = 0x03;

    /// <summary>SRTCP salting key label.</summary>
    public const byte LabelRtcpSalt = 0x05;

    private const int KdfSaltLength = 14;

    /// <summary>
    /// Derives <paramref name="outputLength"/> octets of key material for <paramref name="label"/> from
    /// <paramref name="masterKey"/> (16 or 32 octets → AES-128/256) and <paramref name="masterSalt"/>
    /// (≤ 14 octets; zero-padded to 14).
    /// </summary>
    public static byte[] Derive(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> masterSalt, byte label, int outputLength)
    {
        // x = (master salt padded to 14 octets) with the label XORed into octet 7, then the 14-octet x
        // becomes the high 14 octets of the 16-octet AES-CM counter block (x * 2^16); the low 2 octets are
        // the block counter (RFC 3711 §4.3.3).
        Span<byte> counter = stackalloc byte[16];
        counter.Clear();
        masterSalt[..Math.Min(masterSalt.Length, KdfSaltLength)].CopyTo(counter);
        counter[7] ^= label;

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Key = masterKey.ToArray();

        int blocks = (outputLength + 15) / 16;
        byte[] output = new byte[blocks * 16];
        Span<byte> block = stackalloc byte[16];
        for (int i = 0; i < blocks; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(counter[14..], (ushort)i);
            aes.EncryptEcb(counter, block, PaddingMode.None);
            block.CopyTo(output.AsSpan(i * 16));
        }

        return output[..outputLength];
    }
}
