using System.Net;
using System.Text;
using Agash.StreamTransport.WebRtc.Stun;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// Exercises the STUN message reader/writer against the published RFC 5769 test vectors - the canonical
/// oracle for STUN encoding, XOR-MAPPED-ADDRESS, MESSAGE-INTEGRITY (HMAC-SHA1) and FINGERPRINT (CRC-32).
/// </summary>
[TestClass]
public sealed class StunCodecTests
{
    // RFC 5769 short-term credential password shared by §2.1/§2.2/§2.3.
    private static readonly byte[] ShortTermKey = Encoding.UTF8.GetBytes("VOkJxbRl1RmTxUk/WvJxBt");

    [TestMethod]
    public void EncodeType_BindingRequestAndSuccess_RoundTrip()
    {
        ushort request = StunHeader.EncodeType(StunMessageClass.Request, StunMethod.Binding);
        ushort success = StunHeader.EncodeType(StunMessageClass.SuccessResponse, StunMethod.Binding);

        Assert.AreEqual(0x0001, request);
        Assert.AreEqual(0x0101, success);

        (StunMessageClass cls, StunMethod method) = StunHeader.DecodeType(success);
        Assert.AreEqual(StunMessageClass.SuccessResponse, cls);
        Assert.AreEqual(StunMethod.Binding, method);
    }

    [TestMethod]
    public void TryParse_Ipv4Response_DecodesAddressAndValidatesChecks()
    {
        // RFC 5769 §2.2 "Sample IPv4 Response".
        byte[] message = Hex(
            "010100 3c" +
            "2112a442" +
            "b7e7a701bc34d686fa87dfae" +
            "8022000b 7465737420766563746f7220" +   // SOFTWARE "test vector" + 0x20 pad
            "00200008 0001a147 e112a643" +           // XOR-MAPPED-ADDRESS 192.0.2.1:32853
            "00080014 2b91f599fd9e90c38c7489f92af9ba53f06be7d7" + // MESSAGE-INTEGRITY
            "80280004 c07d4c96");                     // FINGERPRINT

        Assert.IsTrue(StunMessageReader.TryParse(message, out StunMessageReader reader));
        Assert.AreEqual(StunMessageClass.SuccessResponse, reader.Class);
        Assert.AreEqual(StunMethod.Binding, reader.Method);

        Assert.IsTrue(reader.TryGetXorMappedAddress(out IPEndPoint endpoint));
        Assert.AreEqual(IPAddress.Parse("192.0.2.1"), endpoint.Address);
        Assert.AreEqual(32853, endpoint.Port);

        Assert.IsTrue(reader.VerifyFingerprint(), "FINGERPRINT mismatch.");
        Assert.IsTrue(reader.VerifyMessageIntegrity(ShortTermKey), "MESSAGE-INTEGRITY mismatch.");
    }

    [TestMethod]
    public void TryParse_Ipv6Response_DecodesAddress()
    {
        // RFC 5769 §2.3 "Sample IPv6 Response".
        byte[] message = Hex(
            "01010048" +
            "2112a442" +
            "b7e7a701bc34d686fa87dfae" +
            "8022000b 7465737420766563746f7220" +   // SOFTWARE "test vector" + 0x20 pad
            "00200014 0002a147 0113a9faa5d3f179bc25f4b5bed2b9d9" + // XOR-MAPPED-ADDRESS
            "00080014 a382954e4be67bf11784c97c8292c275bfe3ed41" +
            "80280004 c8fb0b4c");

        Assert.IsTrue(StunMessageReader.TryParse(message, out StunMessageReader reader));
        Assert.IsTrue(reader.TryGetXorMappedAddress(out IPEndPoint endpoint));
        Assert.AreEqual(IPAddress.Parse("2001:db8:1234:5678:11:2233:4455:6677"), endpoint.Address);
        Assert.AreEqual(32853, endpoint.Port);
        Assert.IsTrue(reader.VerifyFingerprint());
    }

    [TestMethod]
    public void TryParse_SampleRequest_ReadsUsernameAndValidatesIntegrity()
    {
        // RFC 5769 §2.1 "Sample Request".
        byte[] message = Hex(
            "00010058" +
            "2112a442" +
            "b7e7a701bc34d686fa87dfae" +
            "80220010 5354554e207465737420636c69656e74" + // SOFTWARE "STUN test client"
            "00240004 6e0001ff" +                          // PRIORITY
            "80290008 932ff9b151263b36" +                  // ICE-CONTROLLED
            "00060009 6576746a3a683676 59202020" +         // USERNAME "evtj:h6vY"
            "00080014 9aeaa70cbfd8cb56781ef2b5b2d3f249c1b571a2" + // MESSAGE-INTEGRITY
            "80280004 e57a3bcf");                           // FINGERPRINT

        Assert.IsTrue(StunMessageReader.TryParse(message, out StunMessageReader reader));
        Assert.AreEqual(StunMessageClass.Request, reader.Class);

        Assert.IsTrue(reader.TryFindAttribute(StunAttributeType.Username, out ReadOnlySpan<byte> username));
        Assert.AreEqual("evtj:h6vY", Encoding.UTF8.GetString(username));

        Assert.IsTrue(reader.VerifyMessageIntegrity(ShortTermKey));
        Assert.IsTrue(reader.VerifyFingerprint());
    }

    [TestMethod]
    public void TryParse_RejectsNonStun()
    {
        // High two bits set (looks like RTP/DTLS on a muxed socket), and a buffer too short.
        byte[] rtpLike = [0x80, 0x60, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.IsFalse(StunMessageReader.TryParse(rtpLike, out _));
        Assert.IsFalse(StunMessageReader.TryParse([0x00, 0x01], out _));

        // Right shape, wrong magic cookie.
        byte[] badCookie = new byte[20];
        badCookie[0] = 0x00;
        badCookie[1] = 0x01;
        Assert.IsFalse(StunMessageReader.TryParse(badCookie, out _));
    }

    [TestMethod]
    public void Writer_BindingRequestWithFingerprint_ParsesBackAndVerifies()
    {
        byte[] txId = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        byte[] buffer = new byte[128];

        var writer = new StunMessageWriter(buffer, StunMessageClass.Request, StunMethod.Binding, txId);
        writer.AddAttribute(StunAttributeType.Software, Encoding.UTF8.GetBytes("Agash.StreamTransport"));
        writer.AddMessageIntegrity(ShortTermKey);
        writer.AddFingerprint();

        ReadOnlySpan<byte> message = buffer.AsSpan(0, writer.Length);
        Assert.IsTrue(StunMessageReader.TryParse(message, out StunMessageReader reader));
        Assert.AreEqual(StunMessageClass.Request, reader.Class);
        Assert.AreEqual(StunMethod.Binding, reader.Method);
        CollectionAssert.AreEqual(txId, reader.TransactionId.ToArray());
        Assert.IsTrue(reader.VerifyMessageIntegrity(ShortTermKey));
        Assert.IsTrue(reader.VerifyFingerprint());
    }

    [TestMethod]
    public void Writer_XorMappedAddress_MatchesRfcEncoding()
    {
        byte[] txId = [0xb7, 0xe7, 0xa7, 0x01, 0xbc, 0x34, 0xd6, 0x86, 0xfa, 0x87, 0xdf, 0xae];
        byte[] buffer = new byte[64];

        var writer = new StunMessageWriter(buffer, StunMessageClass.SuccessResponse, StunMethod.Binding, txId);
        writer.AddXorMappedAddress(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 32853));

        Assert.IsTrue(StunMessageReader.TryParse(buffer.AsSpan(0, writer.Length), out StunMessageReader reader));
        Assert.IsTrue(reader.TryGetXorMappedAddress(out IPEndPoint endpoint));
        Assert.AreEqual(IPAddress.Parse("192.0.2.1"), endpoint.Address);
        Assert.AreEqual(32853, endpoint.Port);

        Assert.IsTrue(reader.TryFindAttribute(StunAttributeType.XorMappedAddress, out ReadOnlySpan<byte> value));
        CollectionAssert.AreEqual(Hex("0001a147e112a643"), value.ToArray());
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
