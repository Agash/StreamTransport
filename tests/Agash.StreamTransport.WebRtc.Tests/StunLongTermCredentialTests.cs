using System.Text;
using Agash.StreamTransport.Stun;

namespace Agash.StreamTransport.WebRtc.Tests;

// RFC 8489 sections 9.2, 14.4, 14.6 and appendix B.1. The appendix's bytes are wrong as published
// (errata 6268): the length, and the PASSWORD-ALGORITHM attribute the HMAC covers, are missing. These are
// the corrected bytes, which libjuice's test suite also checks.
[TestClass]
public sealed class StunLongTermCredentialTests
{
    private const string Username = "マトリックス";

    private static readonly byte[] Realm = Encoding.UTF8.GetBytes("example.org");

    private static readonly byte[] SampleRequest = Convert.FromHexString(
        "000100902112a44278ad3433c6ad72c029da412e"
            + "001e00204a3cf38fef6992bda952c6780417da0f24819415569e60b205c46e41407f1704"
            + "001500296f624d61744a6f73324141414366"
            + "2f2f3439396b39353464364f4c33346f4c394653547679363473410000000014000b"
            + "6578616d706c652e6f726700"
            + "001d000400020000"
            + "001c0020b5c7bf005b6c52a21c51c5e892f81924136296cb927c43149309278cc6518e65"
    );

    [TestMethod]
    public void Userhash_MatchesAppendixB1()
    {
        Span<byte> userhash = stackalloc byte[32];
        StunLongTermCredential.Userhash(Username, Realm, userhash);

        CollectionAssert.AreEqual(SampleRequest.AsSpan(24, 32).ToArray(), userhash.ToArray());
    }

    // The password after OpaqueString processing is "TheMatrIX"; the algorithm is SHA-256.
    [TestMethod]
    public void MessageIntegritySha256_VerifiesAppendixB1()
    {
        byte[] key = StunLongTermCredential.Key(
            StunPasswordAlgorithm.Sha256,
            Username,
            Realm,
            "TheMatrIX"
        );
        Assert.IsTrue(StunMessageReader.TryParse(SampleRequest, out StunMessageReader request));

        Assert.IsTrue(request.VerifyMessageIntegritySha256(key));
        Assert.IsFalse(
            request.VerifyMessageIntegritySha256(
                StunLongTermCredential.Key(StunPasswordAlgorithm.Sha256, Username, Realm, "wrong")
            )
        );
    }

    // Writing the same attributes with the same key reproduces the sample's HMAC.
    [TestMethod]
    public void AddMessageIntegritySha256_ReproducesAppendixB1()
    {
        byte[] key = StunLongTermCredential.Key(
            StunPasswordAlgorithm.Sha256,
            Username,
            Realm,
            "TheMatrIX"
        );
        byte[] buffer = new byte[256];
        StunMessageWriter writer = new(
            buffer,
            StunMessageClass.Request,
            StunMethod.Binding,
            SampleRequest.AsSpan(8, 12)
        );
        Span<byte> userhash = stackalloc byte[32];
        StunLongTermCredential.Userhash(Username, Realm, userhash);
        writer.AddAttribute(StunAttributeType.Userhash, userhash);
        writer.AddAttribute(StunAttributeType.Nonce, "obMatJos2AAACf//499k954d6OL34oL9FSTvy64sA"u8);
        writer.AddAttribute(StunAttributeType.Realm, Realm);
        Span<byte> algorithm = stackalloc byte[4];
        StunLongTermCredential.WriteAlgorithm(StunPasswordAlgorithm.Sha256, algorithm);
        writer.AddAttribute(StunAttributeType.PasswordAlgorithm, algorithm);
        writer.AddMessageIntegritySha256(key);

        CollectionAssert.AreEqual(SampleRequest, buffer[..writer.Length]);
    }

    // Section 9.2: "obMatJos2" and the base64 of the 24 feature bits, bit 0 the most significant.
    [TestMethod]
    [DataRow("obMatJos2AAACf//499k", false, false)]
    [DataRow("obMatJos2gAAAxyz", true, false)]
    [DataRow("obMatJos2wAAAxyz", true, true)]
    [DataRow("obMatJos2QAAAxyz", false, true)]
    public void NonceCookie_ReadsTheFeatureBits(string nonce, bool algorithms, bool anonymity)
    {
        Assert.IsTrue(
            StunLongTermCredential.TryReadNonceCookie(
                Encoding.ASCII.GetBytes(nonce),
                out StunSecurityFeatures features
            )
        );
        Assert.AreEqual(new StunSecurityFeatures(algorithms, anonymity), features);
    }

    [TestMethod]
    public void NonceCookie_AbsentFromAPlainNonce() =>
        Assert.IsFalse(
            StunLongTermCredential.TryReadNonceCookie("nonce-1"u8, out StunSecurityFeatures _)
        );

    // Section 18.5.1.1: MD5 of username ":" realm ":" password, as section 9.2.2's example gives it.
    [TestMethod]
    public void Md5Key_MatchesTheSection922Example() =>
        CollectionAssert.AreEqual(
            Convert.FromHexString("8493fbc53ba582fb4c044c456bdc40eb"),
            StunLongTermCredential.Key(
                StunPasswordAlgorithm.Md5,
                "user",
                Encoding.UTF8.GetBytes("realm"),
                "pass"
            )
        );
}
