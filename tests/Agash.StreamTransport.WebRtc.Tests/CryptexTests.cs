using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Sdp;
using Agash.StreamTransport.WebRtc.Srtp;

namespace Agash.StreamTransport.WebRtc.Tests;

// RFC 9335 appendix A: Cryptex test vectors, with and without CSRCs, one- and two-byte extensions, for
// AES-CM with HMAC-SHA1 (RFC 3711) and AES-GCM (RFC 7714).
[TestClass]
public sealed class CryptexTests
{
    [TestMethod]
    [DataRow(
        "A.1.1.",
        "900f1235decafbadcafebabebede000151000200abababababababababababababababab",
        "900f1235decafbadcafebabec0de0001eb92365251c3e036f8de27e9c27ee3e0b4651d9fbc4218a70244522f34a5"
    )]
    [DataRow(
        "A.1.2.",
        "900f1236decafbadcafebabe1000000105020002abababababababababababababababab",
        "900f1236decafbadcafebabec2de00014ed9cc4e6a712b3096c5ca77339d4204ce0d77396cab69585fbce38194a5"
    )]
    [DataRow(
        "A.1.3.",
        "920f1238decafbadcafebabe0001e2400000b26ebede000151000200abababababababababababababababab",
        "920f1238decafbadcafebabe8bb6e12b5cff16ddc0de000192838c8c09e58393e1de3a9a74734d6745671338c3acf11da2df8423bee0"
    )]
    [DataRow(
        "A.1.4.",
        "920f1239decafbadcafebabe0001e2400000b26e1000000105020002abababababababababababababababab",
        "920f1239decafbadcafebabef70e513eb90b9b25c2de0001bbed4848faa644665f3d7f34125914e9f4d0ae923c6f479b95a0f7b53133"
    )]
    [DataRow(
        "A.1.5.",
        "920f123adecafbadcafebabe0001e2400000b26ebede0000abababababababababababababababab",
        "920f123adecafbadcafebabe7130b6abfe2ab0e3c0de0000e3d9f64b25c9e74cb4cf8e43fb92e3781c2c0ceab6b3a499a14c"
    )]
    [DataRow(
        "A.1.6.",
        "920f123bdecafbadcafebabe0001e2400000b26e10000000abababababababababababababababab",
        "920f123bdecafbadcafebabecbf24c124330e1c8c2de0000599dd45bc9d687b603e8b59d771fd38e88b170e0cd31e125eabe"
    )]
    public void AesCm_ProtectsAndUnprotectsTheVector(string section, string plain, string encrypted)
    {
        using SrtpCmTransform transform = new(
            Convert.FromHexString("e1f97a0d3e018be0d64fa32c06de4139"),
            Convert.FromHexString("0ec675ad498afeebb6960b3aabe6")
        );
        Check(transform, transform, section, plain, encrypted);
    }

    [TestMethod]
    [DataRow(
        "A.2.1.",
        "900f1235decafbadcafebabebede000151000200abababababababababababababababab",
        "900f1235decafbadcafebabec0de000139972dc9572c4d99e8fc355de743fb2e94f9d8ff54e72f4193bbc5c74ffab0fa9fa0fbeb"
    )]
    [DataRow(
        "A.2.2.",
        "900f1236decafbadcafebabe1000000105020002abababababababababababababababab",
        "900f1236decafbadcafebabec2de0001bb75a4c545cd1f413bdb7daa2b1e3263de313667c963249081b35a65f5cb6c88b394235f"
    )]
    [DataRow(
        "A.2.3.",
        "920f1238decafbadcafebabe0001e2400000b26ebede000151000200abababababababababababababababab",
        "920f1238decafbadcafebabe63bbccc4a7f695c4c0de00018ad7c71fac70a80c92866b4c6ba98546ef913586e95ffaaffe956885bb0647a8bc094ac8"
    )]
    [DataRow(
        "A.2.4.",
        "920f1239decafbadcafebabe0001e2400000b26e1000000105020002abababababababababababababababab",
        "920f1239decafbadcafebabe3680524f8d312b00c2de0001c78d120038422bc111a7187a18246f980c059cc6bc9df8b626394eca344e4b05d80fea83"
    )]
    [DataRow(
        "A.2.5.",
        "920f123adecafbadcafebabe0001e2400000b26ebede0000abababababababababababababababab",
        "920f123adecafbadcafebabe15b6bb4337906fffc0de0000b7b964537a2b03ab7ba5389ce93317126b5d974df30c6884dcb651c5e120c1da"
    )]
    [DataRow(
        "A.2.6.",
        "920f123bdecafbadcafebabe0001e2400000b26e10000000abababababababababababababababab",
        "920f123bdecafbadcafebabedcb38c9e48bf95f4c2de000061ee432cf920317076613258d3ce4236c06ac429681ad08413512dc98b5207d8"
    )]
    public void AesGcm_ProtectsAndUnprotectsTheVector(
        string section,
        string plain,
        string encrypted
    )
    {
        using var transform = SrtpGcmTransform.FromMaster(
            Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
            Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaab")
        );
        Check(transform, transform, section, plain, encrypted);
    }

    // Without Cryptex asked for, the same packet goes as plain SRTP, its extensions in the clear.
    [TestMethod]
    public void WithoutCryptex_ExtensionsStayInTheClear()
    {
        using var transform = SrtpGcmTransform.FromMaster(
            Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
            Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaab")
        );
        byte[] plain = Convert.FromHexString(
            "900f1235decafbadcafebabebede000151000200abababababababababababababababab"
        );
        byte[] buffer = new byte[plain.Length + 16];
        plain.CopyTo(buffer, 0);

        _ = transform.ProtectRtp(0, buffer, plain.Length, cryptex: false);

        CollectionAssert.AreEqual(plain[..20], buffer[..20]);
    }

    private static readonly RtcCertificate Certificate = RtcCertificate.Generate();

    private static readonly SdpCodec H264 = new(
        96,
        "H264",
        90000,
        null,
        "packetization-mode=1",
        ["nack", "nack pli"]
    );

    public TestContext TestContext { get; set; } = null!;

    // Section 4: negotiated both ways, the wire shows a 0xC0DE block and no MID; with either side off, the
    // RFC 8285 block and its MID travel in the clear.
    [TestMethod]
    [Timeout(60_000)]
    [DataRow(true, true, true)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    public async Task Negotiated_HidesTheExtensionsOnTheWire(
        bool senderCryptex,
        bool receiverCryptex,
        bool hidden
    )
    {
        InMemoryIceNetwork network = new();
        ConcurrentQueue<byte[]> wire = new();
        // RFC 7983 section 7: RTP by its version; a DTLS 1.3 record can carry 96 in its second octet.
        network.Drop = (from, _, data) =>
        {
            if (
                from.Address.Equals(IPAddress.Parse("10.0.0.1"))
                && data.Length > 16
                && data[0] >> 6 == 2
                && (data[1] & 0x7F) == 96
            )
            {
                wire.Enqueue([.. data]);
            }

            return false;
        };
        await using PeerConnection sender = Peer(network, "10.0.0.1", 0xAAAA_0001, senderCryptex);
        await using PeerConnection receiver = Peer(
            network,
            "10.0.0.2",
            0xBBBB_0001,
            receiverCryptex
        );
        sender.LocalIceCandidate += receiver.AddRemoteIceCandidate;
        receiver.LocalIceCandidate += sender.AddRemoteIceCandidate;
        TaskCompletionSource<RtpHeader> received = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        receiver.RtpReceived += (header, _) => received.TrySetResult(header);
        receiver.SetRemoteDescription(sender.CreateOffer(), SdpType.Offer);
        sender.SetRemoteDescription(receiver.CreateAnswer(), SdpType.Answer);

        using PeriodicTimer frames = new(TimeSpan.FromMilliseconds(20));
        for (
            uint i = 0;
            !received.Task.IsCompleted
                && await frames.WaitForNextTickAsync(TestContext.CancellationToken);
            i++
        )
        {
            _ = sender.TrySendRtp(96, 0xAAAA_0001, i * 3000, true, [0x65, 1, 2, 3]);
        }

        RtpHeader header = await received.Task;
        Assert.AreEqual("0", header.Mid, "the receiver reads the MID either way");
        byte[] packet = wire.First();
        ushort profile = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(12));
        Assert.AreEqual(
            hidden ? Cryptex.OneByteProfile : (ushort)0xBEDE,
            profile,
            string.Join(
                " | ",
                wire.Take(4).Select(static p => Convert.ToHexString(p.AsSpan(0, 24)))
            )
        );
        Assert.AreEqual(!hidden, ContainsMid(packet), "MID in the clear");
    }

    // A one-byte MID element for "0": id and length nibble, then the tag.
    private static bool ContainsMid(byte[] packet)
    {
        int words = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(14));
        ReadOnlySpan<byte> block = packet.AsSpan(16, words * 4);
        byte element = (byte)(RtpExtensionMap.Offered.Mid << 4);
        for (int i = 0; i + 1 < block.Length; i++)
        {
            if (block[i] == element && block[i + 1] == (byte)'0')
            {
                return true;
            }
        }

        return false;
    }

    private static PeerConnection Peer(
        InMemoryIceNetwork network,
        string address,
        uint ssrc,
        bool cryptex
    ) =>
        new(
            new PeerConnectionOptions
            {
                Media = [new MediaLine("0", SdpMediaKind.Video, ssrc, [H264])],
                SocketFactory = network.Factory(IPAddress.Parse(address)),
                Cryptex = cryptex,
            },
            Certificate
        );

    private static void Check(
        ISrtpTransform protect,
        ISrtpTransform unprotect,
        string section,
        string plain,
        string encrypted
    )
    {
        byte[] expected = Convert.FromHexString(encrypted);
        byte[] buffer = new byte[expected.Length];
        Convert.FromHexString(plain).CopyTo(buffer, 0);

        int length = protect.ProtectRtp(0, buffer, plain.Length / 2, cryptex: true);

        CollectionAssert.AreEqual(expected, buffer[..length], section);
        Assert.IsTrue(unprotect.UnprotectRtp(0, buffer, length, out int plaintextLength), section);
        CollectionAssert.AreEqual(Convert.FromHexString(plain), buffer[..plaintextLength], section);
    }
}
