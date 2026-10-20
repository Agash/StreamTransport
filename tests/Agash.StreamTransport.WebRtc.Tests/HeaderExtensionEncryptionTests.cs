using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Sdp;
using Agash.StreamTransport.WebRtc.Srtp;

namespace Agash.StreamTransport.WebRtc.Tests;

// RFC 6904 appendix A, as libsrtp's driver checks it: elements 1, 3 and 4 encrypted, element 2 in the
// clear, with AES-CM and HMAC-SHA1 (RFC 3711) and with AES-GCM, whose header keystream is AES-CM
// (RFC 7714 section 8.3).
[TestClass]
public sealed class HeaderExtensionEncryptionTests
{
    private const string Plaintext =
        "900f1234decafbadcafebabebede0006"
        + "17414273a475262748220000c8308e46"
        + "55996386b395fb00"
        + "abababababababababababababababab";

    private static readonly byte[] MasterKey = Convert.FromHexString(
        "e1f97a0d3e018be0d64fa32c06de4139"
    );

    private static readonly RtpExtensionIdSet Encrypted = default(RtpExtensionIdSet)
        .With(1)
        .With(3)
        .With(4);

    [TestMethod]
    public void AesCm_EncryptsTheChosenElements()
    {
        using SrtpCmTransform transform = new(
            MasterKey,
            Convert.FromHexString("0ec675ad498afeebb6960b3aabe6")
        );

        Check(
            transform,
            "900f1234decafbadcafebabebede0006"
                + "17588a9270f4e15e1c220000c83095"
                + "46a994f0bc547897004e55dc4ce79978d88ca4d215949d24025a46b3ca35c535a891c7"
        );
    }

    [TestMethod]
    public void AesGcm_EncryptsTheChosenElementsWithCounterMode()
    {
        using var transform = SrtpGcmTransform.FromMaster(
            MasterKey,
            Convert.FromHexString("0ec675ad498afeebb6960b3a")
        );

        Check(
            transform,
            "900f1234decafbadcafebabebede0006"
                + "1712e0205bfa949b1c220000c830bb46"
                + "732778d9929aab00"
                + "0eca0cf95ee955b26cd3d288b49f6ca9f4b1b759719eb5bc113b9ff1d40cd25a"
        );
    }

    // An element left out of the set travels in the clear, and so does everything without a set.
    [TestMethod]
    public void WithoutTheSet_TheBlockStaysInTheClear()
    {
        using SrtpCmTransform transform = new(
            MasterKey,
            Convert.FromHexString("0ec675ad498afeebb6960b3aabe6")
        );
        byte[] buffer = new byte[66];
        Convert.FromHexString(Plaintext).CopyTo(buffer, 0);

        _ = transform.ProtectRtp(0, buffer, 56, cryptex: false);

        CollectionAssert.AreEqual(Convert.FromHexString(Plaintext)[..40], buffer[..40]);
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

    // Section 4: both sides offering encrypted elements and no Cryptex, the capture time's element header
    // stays readable on the wire and its value does not, and the receiver reads the value back.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Negotiated_EncryptsTheCaptureTimeOnTheWire()
    {
        const ulong Capture = 0x0123_4567_89AB_CDEF;
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
        await using PeerConnection sender = Peer(network, "10.0.0.1", 0xAAAA_0001);
        await using PeerConnection receiver = Peer(network, "10.0.0.2", 0xBBBB_0001);
        sender.LocalIceCandidate += receiver.AddRemoteIceCandidate;
        receiver.LocalIceCandidate += sender.AddRemoteIceCandidate;
        TaskCompletionSource<RtpHeader> received = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        receiver.RtpReceived += (header, _) =>
        {
            if (header.AbsoluteCaptureTimeNtp is not null)
            {
                received.TrySetResult(header);
            }
        };
        receiver.SetRemoteDescription(sender.CreateOffer(), SdpType.Offer);
        sender.SetRemoteDescription(receiver.CreateAnswer(), SdpType.Answer);
        Assert.IsTrue(
            sender.NegotiatedExtensions.Encrypted.Contains(
                sender.NegotiatedExtensions.AbsoluteCaptureTime
            )
        );

        using PeriodicTimer frames = new(TimeSpan.FromMilliseconds(20));
        for (
            uint i = 0;
            !received.Task.IsCompleted
                && await frames.WaitForNextTickAsync(TestContext.CancellationToken);
            i++
        )
        {
            _ = sender.TrySendRtp(
                96,
                0xAAAA_0001,
                i * 3000,
                true,
                [0x65, 1],
                new RtpExtensionValues(Capture)
            );
        }

        Assert.AreEqual(Capture, (await received.Task).AbsoluteCaptureTimeNtp);
        byte[] packet = wire.First(p =>
            BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(12)) == 0xBEDE
        );
        int id = sender.NegotiatedExtensions.AbsoluteCaptureTime;
        int at = packet.AsSpan(16).IndexOf((byte)((id << 4) | 7));
        Assert.IsGreaterThanOrEqualTo(0, at, "the element header is in the clear");
        Assert.AreNotEqual(
            Capture,
            BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(16 + at + 1))
        );
    }

    private static PeerConnection Peer(InMemoryIceNetwork network, string address, uint ssrc) =>
        new(
            new PeerConnectionOptions
            {
                Media = [new MediaLine("0", SdpMediaKind.Video, ssrc, [H264])],
                SocketFactory = network.Factory(IPAddress.Parse(address)),
                Cryptex = false,
                EncryptHeaderExtensions = true,
            },
            Certificate
        );

    private static void Check(ISrtpTransform transform, string ciphertext)
    {
        byte[] expected = Convert.FromHexString(ciphertext);
        byte[] buffer = new byte[expected.Length];
        Convert.FromHexString(Plaintext).CopyTo(buffer, 0);

        int length = transform.ProtectRtp(0, buffer, 56, cryptex: false, Encrypted);

        CollectionAssert.AreEqual(expected, buffer[..length]);
        Assert.IsTrue(transform.UnprotectRtp(0, buffer, length, out int plain, Encrypted));
        CollectionAssert.AreEqual(Convert.FromHexString(Plaintext), buffer[..plain]);
    }
}
