using Agash.StreamTransport.WebRtc.Srtp;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>Validates the AES-CM SRTP transform against RFC 3711 and the libsrtp reference packet.</summary>
[TestClass]
public sealed class SrtpCmTransformTests
{
    // The master key and salt of the RFC 3711 appendix B.3 derivation and the libsrtp driver test.
    private static readonly byte[] MasterKey = Convert.FromHexString(
        "E1F97A0D3E018BE0D64FA32C06DE4139"
    );
    private static readonly byte[] MasterSalt = Convert.FromHexString(
        "0EC675AD498AFEEBB6960B3AABE6"
    );

    private const string ReferencePlaintext =
        "800F1234DECAFBADCAFEBABE" + "ABABABABABABABABABABABABABABABAB";

    private const string ReferenceProtected =
        "800F1234DECAFBADCAFEBABE"
        + "4E55DC4CE79978D88CA4D215949D2402"
        + "B78D6ACC99EA179B8DBB";

    [TestMethod]
    public void ProtectRtp_Rfc3711KeystreamVector_EncryptsWithExpectedKeystream()
    {
        // RFC 3711 appendix B.2: session key and salt given directly, SSRC 0, index 0, so a zero payload
        // encrypts to the keystream itself.
        byte[] key = Convert.FromHexString("2B7E151628AED2A6ABF7158809CF4F3C");
        byte[] salt = Convert.FromHexString("F0F1F2F3F4F5F6F7F8F9FAFBFCFD");
        using var transform = new SrtpCmTransform(
            key,
            salt,
            new byte[20],
            (byte[])key.Clone(),
            (byte[])salt.Clone(),
            new byte[20]
        );
        byte[] buffer = new byte[12 + 48 + SrtpCmTransform.TagLength];
        buffer[0] = 0x80;

        _ = transform.ProtectRtp(0, buffer, 12 + 48);

        Assert.AreEqual(
            "E03EAD0935C95E80E166B16DD92B4EB4"
                + "D23513162B02D0F72A43A2FE4A5F97AB"
                + "41E95B3BB0A2E8DD477901E4FCA894C0",
            Convert.ToHexString(buffer, 12, 48)
        );
    }

    [TestMethod]
    public void ProtectRtp_LibsrtpReferencePacket_MatchesCiphertextAndTag()
    {
        byte[] plaintext = Convert.FromHexString(ReferencePlaintext);
        byte[] buffer = new byte[plaintext.Length + SrtpCmTransform.TagLength];
        plaintext.CopyTo(buffer, 0);
        using var transform = new SrtpCmTransform(MasterKey, MasterSalt);

        int length = transform.ProtectRtp(0, buffer, plaintext.Length);

        Assert.AreEqual(ReferenceProtected, Convert.ToHexString(buffer, 0, length));
    }

    [TestMethod]
    public void UnprotectRtp_LibsrtpReferencePacket_RecoversPlaintext()
    {
        byte[] buffer = Convert.FromHexString(ReferenceProtected);
        using var transform = new SrtpCmTransform(MasterKey, MasterSalt);

        bool ok = transform.UnprotectRtp(0, buffer, buffer.Length, out int length);

        Assert.IsTrue(ok);
        Assert.AreEqual(ReferencePlaintext, Convert.ToHexString(buffer, 0, length));
    }

    [TestMethod]
    public void UnprotectRtp_WrongRolloverCounter_FailsAuthentication()
    {
        byte[] buffer = new byte[12 + 20 + SrtpCmTransform.TagLength];
        buffer[0] = 0x80;
        buffer[11] = 7;
        using var transform = new SrtpCmTransform(MasterKey, MasterSalt);
        int length = transform.ProtectRtp(3, buffer, 32);

        Assert.IsFalse(transform.UnprotectRtp(4, buffer, length, out _));
        Assert.IsTrue(transform.UnprotectRtp(3, buffer, length, out _));
    }

    [TestMethod]
    public void UnprotectRtp_TamperedPayload_FailsAuthentication()
    {
        byte[] buffer = new byte[12 + 20 + SrtpCmTransform.TagLength];
        buffer[0] = 0x80;
        using var transform = new SrtpCmTransform(MasterKey, MasterSalt);
        int length = transform.ProtectRtp(0, buffer, 32);
        buffer[15] ^= 0x40;

        Assert.IsFalse(transform.UnprotectRtp(0, buffer, length, out _));
    }

    [TestMethod]
    public void ProtectRtcp_ThenUnprotect_RoundTripsAndRejectsTampering()
    {
        byte[] rtcp = Convert.FromHexString(
            "81C90007DEADBEEF0102030405060708090A0B0C0D0E0F101112131415161718"
        );
        byte[] buffer = new byte[rtcp.Length + 14];
        rtcp.CopyTo(buffer, 0);
        using var transform = new SrtpCmTransform(MasterKey, MasterSalt);

        int length = transform.ProtectRtcp(9, buffer, rtcp.Length);
        byte[] tampered = (byte[])buffer.Clone();
        tampered[20] ^= 1;

        Assert.AreEqual(rtcp.Length + 14, length);
        Assert.AreEqual(0x80, buffer[rtcp.Length] & 0x80, "the E flag is set");
        CollectionAssert.AreNotEqual(rtcp[8..], buffer[8..rtcp.Length]);
        Assert.IsTrue(transform.UnprotectRtcp(buffer, length, out int recovered));
        CollectionAssert.AreEqual(rtcp, buffer[..recovered]);
        Assert.IsFalse(transform.UnprotectRtcp(tampered, length, out _));
    }

    [TestMethod]
    public void Keystream_InitialCounter_XorsSaltSsrcAndIndex()
    {
        byte[] counter = new byte[16];
        SrtpCmTransform.Keystream.InitialCounter(
            Convert.FromHexString("F0F1F2F3F4F5F6F7F8F9FAFBFCFD"),
            0x11223344,
            0x0000_0001_0002UL,
            counter
        );

        Assert.AreEqual("F0F1F2F3E5D7C5B3F8F9FAFAFCFF0000", Convert.ToHexString(counter));
    }
}
