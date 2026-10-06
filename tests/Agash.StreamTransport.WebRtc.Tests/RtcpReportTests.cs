using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Agash.StreamTransport.WebRtc.Rtcp;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class RtcpReportTests
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

    public TestContext TestContext { get; set; } = null!;

    // RFC 3550 appendix A.1 and A.3: the wrap counts a cycle, the gap counts as lost, and the fraction is
    // the interval's.
    [TestMethod]
    public void Statistics_AcrossAWrapWithTwoLost_ReportsTheLoss()
    {
        RtpReceiveStatistics statistics = new(90_000);
        ushort[] sequences = [65530, 65531, 65532, 65533, 65535, 0, 1, 3, 4, 5];
        long time = 0;
        foreach (ushort sequence in sequences)
        {
            statistics.OnPacket(sequence, (uint)(time * 90), time * 1000);
            time++;
        }

        RtcpReportBlock block = statistics.Report(0x1234, time * 1000)!.Value;

        Assert.AreEqual(0x10005u, block.ExtendedHighestSequence);
        Assert.AreEqual(2, block.CumulativeLost);
        Assert.AreEqual((byte)(2 * 256 / 12), block.FractionLost);
        Assert.AreEqual(
            0u,
            block.InterarrivalJitter,
            "packets arrived exactly on their timestamps"
        );

        // The next interval starts from here: nothing new, nothing lost.
        Assert.AreEqual((byte)0, statistics.Report(0x1234, time * 1000)!.Value.FractionLost);
    }

    // RFC 3550 appendix A.8: arrivals that drift from the timestamps raise the jitter estimate.
    [TestMethod]
    public void Statistics_UnevenArrivals_RaiseTheJitter()
    {
        RtpReceiveStatistics statistics = new(90_000);
        for (int i = 0; i < 50; i++)
        {
            long arrival = (i * 20_000) + (i % 2 == 0 ? 0 : 5_000);
            statistics.OnPacket((ushort)i, (uint)(i * 1800), arrival);
        }

        uint jitter = statistics.Report(1, 1_000_000)!.Value.InterarrivalJitter;
        Assert.IsGreaterThan(200u, jitter, "5 ms of alternating delay is 450 ticks at 90 kHz");
    }

    // The block echoes the middle 32 bits of the last sender report and the delay since it in 1/65536 s.
    [TestMethod]
    public void Statistics_AfterASenderReport_EchoesItsTiming()
    {
        RtpReceiveStatistics statistics = new(48_000);
        statistics.OnPacket(1, 0, 0);
        const ulong ntp = 0x0123_4567_89AB_CDEF;
        statistics.OnSenderReport(ntp, 1_000_000);

        RtcpReportBlock block = statistics.Report(1, 1_500_000)!.Value;

        Assert.AreEqual(0x4567_89ABu, block.LastSenderReport);
        Assert.AreEqual(32_768u, block.DelaySinceLastSenderReport);
    }

    [TestMethod]
    public void SourceDescriptionAndGoodbye_AreWholeRtcpPackets()
    {
        byte[] buffer = new byte[256];
        uint[] sources = [0x1111_1111, 0x2222_2222];
        int sdes = RtcpSourceDescription.WriteCname(buffer, sources, "abcdefghijklmnop");
        int bye = RtcpSourceDescription.WriteGoodbye(buffer.AsSpan(sdes), sources);

        Assert.AreEqual(0, sdes % 4);
        Assert.AreEqual(RtcpSourceDescription.CnameLength(2, "abcdefghijklmnop"), sdes);
        List<(RtcpPacketType Type, int Count)> packets = [];
        foreach (RtcpElement element in RtcpCompound.Enumerate(buffer.AsSpan(0, sdes + bye)))
        {
            packets.Add((element.PacketType, element.ReportCount));
        }

        CollectionAssert.AreEqual(
            new[] { (RtcpPacketType.SourceDescription, 2), (RtcpPacketType.Goodbye, 2) },
            packets
        );
        StringAssert.Contains(Encoding.ASCII.GetString(buffer, 0, sdes), "abcdefghijklmnop");
    }

    [TestMethod]
    public void RtcpReducedSize_IsOfferedAndRead()
    {
        string text = SdpWriter.Write(Offer());
        StringAssert.Contains(text, "a=rtcp-rsize");
        Assert.IsTrue(SdpReader.TryParse(text, out SdpDescription read));
        Assert.IsTrue(read.Media[0].RtcpReducedSize);
        Assert.IsTrue(SdpReader.TryParse(text.Replace("a=rtcp-rsize\r\n", ""), out read));
        Assert.IsFalse(read.Media[0].RtcpReducedSize);
    }

    // RFC 8888 section 6: the wildcard form, which covers FEC and retransmission payload types too.
    [TestMethod]
    public void CongestionControlFeedback_IsOfferedAsTheWildcardAndRead()
    {
        string text = SdpWriter.Write(Offer());
        StringAssert.Contains(text, "a=rtcp-fb:* ack ccfb");
        Assert.IsTrue(SdpReader.TryParse(text, out SdpDescription read));
        Assert.IsTrue(read.Media[0].CongestionControlFeedback);

        string perCodec = text.Replace("a=rtcp-fb:* ack ccfb", "a=rtcp-fb:96 ack ccfb");
        Assert.IsTrue(SdpReader.TryParse(perCodec, out read));
        Assert.IsTrue(
            read.Media[0].CongestionControlFeedback,
            "a per-codec listing means the same"
        );

        Assert.IsTrue(SdpReader.TryParse(text.Replace("a=rtcp-fb:* ack ccfb\r\n", ""), out read));
        Assert.IsFalse(read.Media[0].CongestionControlFeedback);
    }

    [TestMethod]
    public void WildcardFeedback_AppliesToEveryCodec()
    {
        string text = SdpWriter.Write(Offer()) + "a=rtcp-fb:* goog-remb\r\n";
        Assert.IsTrue(SdpReader.TryParse(text, out SdpDescription read));
        Assert.Contains("goog-remb", read.Media[0].Codecs[0].RtcpFeedback);
    }

    // An answer lists rtcp-rsize and ccfb only when the offer did.
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Answer_EchoesTheOfferedRtcpCapabilities(bool offered)
    {
        await using PeerConnection offerer = new(
            new PeerConnectionOptions
            {
                Media = [new MediaLine("0", SdpMediaKind.Video, 0xAAAA_0001, [H264])],
            },
            Certificate
        );
        await using PeerConnection answerer = new(
            new PeerConnectionOptions
            {
                Media = [new MediaLine("0", SdpMediaKind.Video, 0xBBBB_0001, [H264])],
            },
            Certificate
        );
        string text = SdpWriter.Write(offerer.CreateOffer());
        if (!offered)
        {
            text = text.Replace("a=rtcp-rsize\r\n", "").Replace("a=rtcp-fb:* ack ccfb\r\n", "");
        }

        Assert.IsTrue(SdpReader.TryParse(text, out SdpDescription offer));
        answerer.SetRemoteDescription(offer, SdpType.Offer);
        SdpMediaDescription answered = answerer.CreateAnswer().Media[0];

        Assert.AreEqual(offered, answered.RtcpReducedSize);
        Assert.AreEqual(offered, answered.CongestionControlFeedback);
    }

    // The receiver's reception report echoes the sender's report, which gives the sender the round trip.
    [TestMethod]
    [Timeout(60_000)]
    public async Task SenderAndReceiverReports_MeasureTheRoundTrip()
    {
        await using Pair pair = await Pair.ConnectAsync(reducedSize: true);
        using PeriodicTimer frames = new(TimeSpan.FromMilliseconds(20));
        using CancellationTokenSource expiry = new(TimeSpan.FromSeconds(15));
        uint timestamp = 0;
        while (
            pair.Sender.ReportedRoundTripTime == TimeSpan.Zero
            && await frames.WaitForNextTickAsync(expiry.Token)
        )
        {
            Assert.IsTrue(
                pair.Sender.TrySendRtp(96, 0xAAAA_0001, timestamp += 1800, true, [0x65, 1])
            );
        }

        Assert.IsGreaterThan(TimeSpan.Zero, pair.Sender.ReportedRoundTripTime);
        Assert.IsLessThan(TimeSpan.FromSeconds(1), pair.Sender.ReportedRoundTripTime);
        Assert.Contains(RtcpPacketType.SenderReport, pair.FirstTypesFrom("10.0.0.1"));
    }

    // Without rtcp-rsize on both sides, every RTCP packet starts with a report: feedback goes out compound.
    [TestMethod]
    [Timeout(60_000)]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Feedback_IsCompoundUnlessReducedSizeWasAgreed(bool reducedSize)
    {
        await using Pair pair = await Pair.ConnectAsync(reducedSize);
        using PeriodicTimer frames = new(TimeSpan.FromMilliseconds(20));
        for (int i = 0; i < 25; i++)
        {
            _ = await frames.WaitForNextTickAsync(TestContext.CancellationToken);
            Assert.IsTrue(
                pair.Sender.TrySendRtp(96, 0xAAAA_0001, (uint)(i * 1800), true, [0x65, 1])
            );
        }

        RtcpPacketType[] first = pair.FirstTypesFrom("10.0.0.2");
        if (reducedSize)
        {
            Assert.Contains(RtcpPacketType.TransportFeedback, first, "feedback alone");
        }
        else
        {
            Assert.IsTrue(
                first.All(static t =>
                    t is RtcpPacketType.SenderReport or RtcpPacketType.ReceiverReport
                ),
                $"compound packets start with a report: {string.Join(", ", first)}"
            );
        }
    }

    private static SdpDescription Offer() =>
        new()
        {
            Media =
            [
                new SdpMediaDescription
                {
                    Kind = SdpMediaKind.Video,
                    Mid = "0",
                    Codecs = [H264],
                    IceUfrag = "ufrag",
                    IcePwd = "passwordpasswordpassword",
                    Fingerprint = Certificate.Fingerprint,
                    Ssrc = 0xAAAA_0001,
                    Cname = "test",
                },
            ],
        };

    // Two connections on the in-memory network, noting the type of each RTCP packet's first element per
    // source address.
    private sealed class Pair : IAsyncDisposable
    {
        private readonly ConcurrentQueue<(string From, RtcpPacketType Type)> _rtcp = new();

        private Pair(PeerConnection sender, PeerConnection receiver)
        {
            Sender = sender;
            Receiver = receiver;
        }

        public PeerConnection Sender { get; }

        public PeerConnection Receiver { get; }

        public static async Task<Pair> ConnectAsync(bool reducedSize)
        {
            InMemoryIceNetwork network = new();
            PeerConnection sender = new(
                new PeerConnectionOptions
                {
                    Media = [new MediaLine("0", SdpMediaKind.Video, 0xAAAA_0001, [H264])],
                    SocketFactory = network.Factory(IPAddress.Parse("10.0.0.1")),
                },
                Certificate,
                controller: new CongestionControl.ScreamCongestionController()
            );
            PeerConnection receiver = new(
                new PeerConnectionOptions
                {
                    Media = [new MediaLine("0", SdpMediaKind.Video, 0xBBBB_0001, [H264])],
                    SocketFactory = network.Factory(IPAddress.Parse("10.0.0.2")),
                },
                Certificate
            );
            Pair pair = new(sender, receiver);
            network.Drop = (from, _, data) =>
            {
                // Under rtcp-mux the second byte tells RTCP from RTP (RFC 5761 section 4): SRTCP leaves the
                // first packet's header in the clear.
                if (data.Length >= 8 && data[0] >> 6 == 2 && data[1] is >= 200 and <= 206)
                {
                    pair._rtcp.Enqueue((from.Address.ToString(), (RtcpPacketType)data[1]));
                }

                return false;
            };
            sender.LocalIceCandidate += receiver.AddRemoteIceCandidate;
            receiver.LocalIceCandidate += sender.AddRemoteIceCandidate;
            TaskCompletionSource connected = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
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
            SdpDescription offer = sender.CreateOffer();
            if (!reducedSize)
            {
                Assert.IsTrue(
                    SdpReader.TryParse(
                        SdpWriter.Write(offer).Replace("a=rtcp-rsize\r\n", ""),
                        out offer
                    )
                );
            }

            receiver.SetRemoteDescription(offer, SdpType.Offer);
            sender.SetRemoteDescription(receiver.CreateAnswer(), SdpType.Answer);
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return pair;
        }

        public RtcpPacketType[] FirstTypesFrom(string address) =>
            [.. _rtcp.Where(r => r.From == address).Select(static r => r.Type)];

        public async ValueTask DisposeAsync()
        {
            await Sender.DisposeAsync();
            await Receiver.DisposeAsync();
        }
    }
}
