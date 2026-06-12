using Agash.StreamTransport.WebRtc.Srtp;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>Validates the AES-GCM SRTP transform against the RFC 7714 §16 test vectors.</summary>
[TestClass]
public sealed class SrtpGcmTransformTests
{
    // RFC 7714 §16.1.1: AEAD_AES_128_GCM, session key + salt given directly.
    private static readonly byte[] Key = Hex("000102030405060708090a0b0c0d0e0f");
    private static readonly byte[] Salt = Hex("517569642070726f2071756f");

    // The cleartext RTP packet (12-octet header + payload).
    private static readonly byte[] Plaintext = Hex(
        "8040f17b 8041f8d3 5501a0b2 47616c6c 69612065 7374206f 6d6e6973" +
        "20646976 69736120 696e2070 61727465 73207472 6573");

    // header || ciphertext || 16-octet GMAC tag.
    private static readonly byte[] Protected = Hex(
        "8040f17b 8041f8d3 5501a0b2" +
        "f24de3a3 fb34de6c acba861c 9d7e4bca be633bd5 0d294e6f 42a5f47a 51c7d19b 36de3adf 8833" +
        "899d7f27 beb16a91 52cf765e e4390cce");

    [TestMethod]
    public void ProtectRtp_Rfc7714Vector_ProducesExpectedCipherAndTag()
    {
        byte[] buffer = new byte[Plaintext.Length + SrtpGcmTransform.TagLength];
        Plaintext.CopyTo(buffer, 0);

        int length = SrtpGcmTransform.ProtectRtp(Key, Salt, rolloverCounter: 0, buffer, Plaintext.Length);

        Assert.AreEqual(Protected.Length, length);
        CollectionAssert.AreEqual(Protected, buffer[..length]);
    }

    [TestMethod]
    public void UnprotectRtp_Rfc7714Vector_RecoversPlaintext()
    {
        byte[] buffer = (byte[])Protected.Clone();

        bool ok = SrtpGcmTransform.UnprotectRtp(Key, Salt, rolloverCounter: 0, buffer, Protected.Length, out int plaintextLength);

        Assert.IsTrue(ok, "authentication should succeed for a genuine packet");
        Assert.AreEqual(Plaintext.Length, plaintextLength);
        CollectionAssert.AreEqual(Plaintext, buffer[..plaintextLength]);
    }

    [TestMethod]
    public void UnprotectRtp_TamperedTag_FailsAuthentication()
    {
        byte[] buffer = (byte[])Protected.Clone();
        buffer[^1] ^= 0xFF; // corrupt the tag

        bool ok = SrtpGcmTransform.UnprotectRtp(Key, Salt, rolloverCounter: 0, buffer, buffer.Length, out _);
        Assert.IsFalse(ok);
    }

    [TestMethod]
    public void ProtectThenUnprotect_WithCsrcsAndExtension_RoundTrips()
    {
        // V=2, P=0, X=1, CC=2 header (0xB2), so header = 12 + 8 (CSRC) + extension.
        byte[] packet =
        [
            0xB2, 0x60, 0x12, 0x34,             // V/P/X/CC, M/PT, seq
            0x00, 0x00, 0x00, 0x10,             // timestamp
            0xDE, 0xAD, 0xBE, 0xEF,             // ssrc
            0x11, 0x11, 0x11, 0x11, 0x22, 0x22, 0x22, 0x22, // 2 CSRCs
            0xBE, 0xDE, 0x00, 0x01, 0x10, 0xAA, 0x00, 0x00, // extension: 1 word
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, // payload
        ];
        int originalLength = packet.Length;
        byte[] buffer = new byte[originalLength + SrtpGcmTransform.TagLength];
        packet.CopyTo(buffer, 0);

        int protectedLength = SrtpGcmTransform.ProtectRtp(Key, Salt, 7, buffer, originalLength);
        // header + extension must be unchanged (authenticated, not encrypted).
        CollectionAssert.AreEqual(packet[..28], buffer[..28]);

        bool ok = SrtpGcmTransform.UnprotectRtp(Key, Salt, 7, buffer, protectedLength, out int recovered);
        Assert.IsTrue(ok);
        Assert.AreEqual(originalLength, recovered);
        CollectionAssert.AreEqual(packet, buffer[..recovered]);
    }

    private static byte[] Hex(string hex)
    {
        Span<char> compact = stackalloc char[hex.Length];
        int n = 0;
        foreach (char c in hex)
        {
            if (!char.IsWhiteSpace(c))
            {
                compact[n++] = c;
            }
        }

        return Convert.FromHexString(compact[..n]);
    }
}
