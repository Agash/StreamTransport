using System.Collections.Concurrent;
using System.Net;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class RtxNegotiationTests
{
    private static readonly RtcCertificate Certificate = RtcCertificate.Generate();

    private static readonly SdpCodec H264 = new(
        96,
        "H264",
        90000,
        null,
        "packetization-mode=1;profile-level-id=42e01f",
        ["nack", "nack pli"]
    );

    [TestMethod]
    public void Sdp_FidGroup_PairsTheMediaAndRtxSsrcs()
    {
        SdpDescription description = Offer(0xAAAA_0001, 0xAAAA_0002);

        string text = SdpWriter.Write(description);
        Assert.IsTrue(SdpReader.TryParse(text, out SdpDescription read));

        StringAssert.Contains(text, "a=ssrc-group:FID 2863267841 2863267842");
        StringAssert.Contains(text, "a=rtpmap:97 rtx/90000");
        StringAssert.Contains(text, "a=fmtp:97 apt=96");
        Assert.AreEqual(0xAAAA_0001u, read.Media[0].Ssrc);
        Assert.AreEqual(0xAAAA_0002u, read.Media[0].RtxSsrc);
    }

    [TestMethod]
    public void Sdp_SsrcLinesWithoutAGroup_KeepTheFirstAsTheMedia()
    {
        SdpDescription description = Offer(0xAAAA_0001, rtxSsrc: null);
        string text = SdpWriter.Write(description) + "a=ssrc:12345 cname:other\r\n";

        Assert.IsTrue(SdpReader.TryParse(text, out SdpDescription read));

        Assert.AreEqual(0xAAAA_0001u, read.Media[0].Ssrc);
        Assert.IsNull(read.Media[0].RtxSsrc);
    }

    [TestMethod]
    public async Task Answer_PairsEachAnsweredCodecWithItsRtx()
    {
        await using var offerer = new PeerConnection(
            Options(0xAAAA_0001, 0xAAAA_0002),
            Certificate
        );
        await using var answerer = new PeerConnection(
            Options(0xBBBB_0001, 0xBBBB_0002),
            Certificate
        );

        answerer.SetRemoteDescription(offerer.CreateOffer(), SdpType.Offer);
        SdpMediaDescription answered = answerer.CreateAnswer().Media[0];

        CollectionAssert.AreEqual(
            new[] { 96, 97 },
            answered.Codecs.Select(static c => c.PayloadType).ToArray()
        );
        Assert.AreEqual(96, Rtx.Repairs(answered.Codecs[1]));
        Assert.AreEqual(0xBBBB_0002u, answered.RtxSsrc);
    }

    [TestMethod]
    public async Task Answer_WithoutRtxOnThisSide_AnswersNone()
    {
        await using var offerer = new PeerConnection(
            Options(0xAAAA_0001, 0xAAAA_0002),
            Certificate
        );
        await using var answerer = new PeerConnection(
            Options(0xBBBB_0001, rtxSsrc: null),
            Certificate
        );

        answerer.SetRemoteDescription(offerer.CreateOffer(), SdpType.Offer);
        SdpMediaDescription answered = answerer.CreateAnswer().Media[0];

        Assert.IsFalse(answered.Codecs.Any(Rtx.IsRtx));
        Assert.IsNull(answered.RtxSsrc);
    }

    // A lost packet is asked for again (NACK), sent on the RTX stream and delivered as the original:
    // the same sequence number and payload type.
    [TestMethod]
    [Timeout(60_000)]
    public async Task LostPacket_IsRecoveredThroughNackAndRtx()
    {
        InMemoryIceNetwork network = new();
        PeerConnectionOptions senderOptions = Options(0xAAAA_0001, 0xAAAA_0002);
        PeerConnectionOptions receiverOptions = Options(0xBBBB_0001, 0xBBBB_0002);
        await using var sender = new PeerConnection(
            new PeerConnectionOptions
            {
                Media = senderOptions.Media,
                SocketFactory = network.Factory(IPAddress.Parse("10.0.0.1")),
            },
            Certificate
        );
        await using var receiver = new PeerConnection(
            new PeerConnectionOptions
            {
                Media = receiverOptions.Media,
                SocketFactory = network.Factory(IPAddress.Parse("10.0.0.2")),
            },
            Certificate
        );
        sender.LocalIceCandidate += receiver.AddRemoteIceCandidate;
        receiver.LocalIceCandidate += sender.AddRemoteIceCandidate;
        TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int up = 0;
        void OnState(PeerConnectionState state)
        {
            if (state == PeerConnectionState.Connected && Interlocked.Increment(ref up) == 2)
            {
                connected.TrySetResult();
            }
        }

        sender.StateChanged += OnState;
        receiver.StateChanged += OnState;
        receiver.SetRemoteDescription(sender.CreateOffer(), SdpType.Offer);
        sender.SetRemoteDescription(receiver.CreateAnswer(), SdpType.Answer);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The network loses the fifth media packet the sender sends, once. RTP headers travel in the
        // clear under SRTP, so the payload type and sequence number are readable here.
        int lost = -1;
        int media = 0;
        network.Drop = (_, _, data) =>
        {
            if (
                data.Length < 12
                || data[0] >> 6 != 2
                || (data[1] & 0x7F) != 96
                || Volatile.Read(ref lost) >= 0
                || Interlocked.Increment(ref media) != 5
            )
            {
                return false;
            }

            Volatile.Write(ref lost, (data[2] << 8) | data[3]);
            return true;
        };
        ConcurrentQueue<(ushort Sequence, byte PayloadType)> received = new();
        TaskCompletionSource recovered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.RtpReceived += (header, _) =>
        {
            received.Enqueue((header.SequenceNumber, header.PayloadType));
            if (header.SequenceNumber == Volatile.Read(ref lost))
            {
                recovered.TrySetResult();
            }
        };

        for (int i = 0; i < 20; i++)
        {
            Assert.IsTrue(
                sender.TrySendRtp(96, 0xAAAA_0001, (uint)(i * 3000), marker: true, [0x65, (byte)i])
            );
        }

        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsGreaterThanOrEqualTo(0, lost);
        Assert.Contains(((ushort)lost, (byte)96), received, "the repair arrives as the original");
        Assert.IsGreaterThan(0, receiver.CurrentLossStats.NackSequencesRequested);
        Assert.IsGreaterThan(0, sender.CurrentLossStats.RtxPacketsSent);
        Assert.IsGreaterThan(
            0,
            sender.SentBytes(TrafficClass.Retransmission),
            "the repair went through the pacer, inside the budget"
        );
        Assert.IsGreaterThan(0, receiver.CurrentLossStats.RtxPacketsRecovered);
    }

    private static PeerConnectionOptions Options(uint ssrc, uint? rtxSsrc) =>
        new()
        {
            Media =
            [
                new MediaLine(
                    "0",
                    SdpMediaKind.Video,
                    ssrc,
                    rtxSsrc is null ? [H264] : [H264, Rtx.For(97, 96)]
                )
                {
                    RtxSsrc = rtxSsrc,
                },
            ],
        };

    private static SdpDescription Offer(uint ssrc, uint? rtxSsrc) =>
        new()
        {
            Media =
            [
                new SdpMediaDescription
                {
                    Kind = SdpMediaKind.Video,
                    Mid = "0",
                    Codecs = rtxSsrc is null ? [H264] : [H264, Rtx.For(97, 96)],
                    IceUfrag = "ufrag",
                    IcePwd = "passwordpasswordpassword",
                    Fingerprint = Certificate.Fingerprint,
                    Ssrc = ssrc,
                    RtxSsrc = rtxSsrc,
                    Cname = "test",
                },
            ],
        };
}
