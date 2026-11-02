using System.Buffers.Binary;
using System.Net;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.WebRtc.Rtcp;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Tests;

// Transport-wide congestion control feedback (draft-holmer-rmcat-transport-wide-cc-extensions-01), which
// browsers send where they do not offer RFC 8888: the packet format, the receiver's tracking, the
// sequence number stamped on leaving packets, and a connection that negotiates it as a browser would.
[TestClass]
public sealed class TransportFeedbackTests
{
    private static readonly RtcCertificate Certificate = RtcCertificate.Generate();

    [TestMethod]
    public void Feedback_RoundTripsEveryKindOfStatus()
    {
        // From the reference time: received, two lost, a late one (large delta), one arriving earlier
        // than the one before it (negative delta), a long received run, a long lost run.
        List<TransportPacketArrival> sent = [];
        long at = 640_000 + 1_000;
        ushort sequence = 65_530;
        sent.Add(new(sequence++, at));
        sent.Add(new(sequence++, null));
        sent.Add(new(sequence++, null));
        at += 90_000;
        sent.Add(new(sequence++, at));
        at -= 2_000;
        sent.Add(new(sequence++, at));
        for (int i = 0; i < 30; i++)
        {
            at += 500;
            sent.Add(new(sequence++, at));
        }

        for (int i = 0; i < 20; i++)
        {
            sent.Add(new(sequence++, null));
        }

        at += 1_000;
        sent.Add(new(sequence++, at));

        byte[] packet = new byte[1500];
        int length = TransportFeedback.Build(
            packet,
            0x1111_1111,
            0x2222_2222,
            7,
            [.. sent],
            1200,
            out int consumed
        );
        Assert.AreEqual(sent.Count, consumed);
        Assert.AreEqual(0, length % 4);
        Assert.AreEqual(0x80 | 15, packet[0]);
        Assert.AreEqual(205, packet[1]);
        Assert.AreEqual(7, packet[19], "the feedback count");

        List<TransportPacketArrival> read = [];
        Assert.IsTrue(TransportFeedback.TryParse(packet.AsSpan(0, length), read));
        Assert.HasCount(sent.Count, read);
        for (int i = 0; i < sent.Count; i++)
        {
            Assert.AreEqual(sent[i].Sequence, read[i].Sequence);
            Assert.AreEqual(
                sent[i].ArrivalMicros is null,
                read[i].ArrivalMicros is null,
                $"packet {i}"
            );
            if (sent[i].ArrivalMicros is { } expected)
            {
                // The format keeps 250 µs, and each delta is from the previous rounded arrival.
                Assert.AreEqual(expected, read[i].ArrivalMicros!.Value, 250, $"packet {i}");
            }
        }
    }

    // The wire layout of a small report: base, count, a reference time in 64 ms units, one status vector
    // chunk, then one-byte deltas.
    [TestMethod]
    public void Feedback_LaysOutTheFixedPartAndChunks()
    {
        byte[] packet = new byte[64];
        int length = TransportFeedback.Build(
            packet,
            1,
            2,
            0,
            [new(100, 128_000), new(101, null), new(102, 128_750)],
            1200,
            out _
        );

        Assert.AreEqual(100, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(12)));
        Assert.AreEqual(3, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(14)));
        Assert.AreEqual(
            2,
            (packet[16] << 16) | (packet[17] << 8) | packet[18],
            "128 ms is two units"
        );

        // One-bit status vector: received, lost, received.
        Assert.AreEqual(
            0x8000 | (1 << 13) | (1 << 11),
            BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(20))
        );
        Assert.AreEqual(0, packet[22], "the first arrives at the reference time");
        Assert.AreEqual(3, packet[23], "750 µs later, in 250 µs units");
        Assert.AreEqual(24, length);
    }

    [TestMethod]
    public void Feedback_LongReports_AreSplitAtTheSizeLimit()
    {
        TransportPacketArrival[] arrivals =
        [
            .. Enumerable
                .Range(0, 2000)
                .Select(i => new TransportPacketArrival((ushort)i, i * 80_000L)),
        ];
        byte[] packet = new byte[1500];

        int length = TransportFeedback.Build(packet, 1, 2, 0, arrivals, 300, out int consumed);

        Assert.IsLessThanOrEqualTo(300, length);
        Assert.IsGreaterThan(0, consumed);
        Assert.IsLessThan(arrivals.Length, consumed);
        List<TransportPacketArrival> read = [];
        Assert.IsTrue(TransportFeedback.TryParse(packet.AsSpan(0, length), read));
        Assert.HasCount(consumed, read);
    }

    [TestMethod]
    public void Tracker_ReportsLateArrivalsAgain_AsReceived()
    {
        TransportFeedbackTracker tracker = new();
        tracker.OnPacket(10, 1_000);
        tracker.OnPacket(12, 3_000);
        List<TransportPacketArrival> first = [];
        tracker.TakeReport(first);
        Assert.AreEqual(3, first.Count);
        Assert.IsNull(first[1].ArrivalMicros);

        tracker.OnPacket(11, 4_000);
        tracker.OnPacket(13, 5_000);
        List<TransportPacketArrival> second = [];
        tracker.TakeReport(second);

        Assert.AreEqual((ushort)11, second[0].Sequence, "the late packet starts the next report");
        Assert.AreEqual(4_000, second[0].ArrivalMicros);
        Assert.AreEqual(3, second.Count);
        Assert.IsFalse(tracker.HasNew);
    }

    [TestMethod]
    public void Tracker_CountsOnAcrossTheSequenceWrap()
    {
        TransportFeedbackTracker tracker = new();
        tracker.OnPacket(65_535, 1_000);
        tracker.OnPacket(0, 2_000);
        tracker.OnPacket(1, 3_000);
        List<TransportPacketArrival> report = [];
        tracker.TakeReport(report);

        CollectionAssert.AreEqual(
            new ushort[] { 65_535, 0, 1 },
            report.Select(static a => a.Sequence).ToArray()
        );
    }

    // The number goes in as the packet leaves, whatever extension block it has or lacks, and reads back.
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Stamp_AddsTheSequenceToAnyPacket(bool withCaptureTime)
    {
        RtpExtensionMap map = RtpExtensionMap.Offered;
        byte[] packet = new byte[200];
        byte[] payload = [1, 2, 3, 4, 5];
        int length = RtpPacket.Write(
            packet,
            true,
            96,
            7,
            90_000,
            0xABCD,
            payload,
            map,
            withCaptureTime ? new RtpExtensionValues(AbsoluteCaptureTimeNtp: 42) : default
        );

        int stamped = map.InsertTransportSequence(packet, length, 0xBEEF);

        // With a capture time the element fits the block's padding; without a block it brings one.
        Assert.AreEqual(withCaptureTime ? length : length + 8, stamped);
        Assert.IsLessThanOrEqualTo(length + map.MaximumBlockLength, stamped);
        Assert.IsTrue(
            RtpPacket.TryParse(
                packet.AsSpan(0, stamped),
                map,
                out RtpHeader header,
                out ReadOnlySpan<byte> read
            )
        );
        Assert.AreEqual((ushort)0xBEEF, header.Extensions.TransportSequence);
        Assert.AreEqual(withCaptureTime ? 42UL : null, header.Extensions.AbsoluteCaptureTimeNtp);
        CollectionAssert.AreEqual(payload, read.ToArray());
        Assert.IsTrue(header.Marker);
        Assert.AreEqual((ushort)7, header.SequenceNumber);
    }

    // A peer that offers transport-cc and not RFC 8888, as Chrome does, gets transport feedback both ways,
    // and the sender's controller hears about the packets it sent.
    [TestMethod]
    [Timeout(90_000)]
    public async Task BrowserLikePeer_FeedsTheControllerThroughTransportFeedback()
    {
        InMemoryIceNetwork network = new();
        var videoCodec = new SdpCodec(96, "H265", 90000, null, null, ["nack", "nack pli"]);
        RecordingController controller = new();
        await using var sender = new PeerConnection(
            new PeerConnectionOptions
            {
                SocketFactory = network.Factory(IPAddress.Parse("10.0.0.1")),
                Media =
                [
                    new MediaLine("0", SdpMediaKind.Video, LocalSsrc: 0xCCCC_0001, [videoCodec]),
                ],
            },
            Certificate,
            controller: controller
        );
        await using var receiver = new PeerConnection(
            new PeerConnectionOptions
            {
                SocketFactory = network.Factory(IPAddress.Parse("10.0.0.2")),
                Media =
                [
                    new MediaLine("0", SdpMediaKind.Video, LocalSsrc: 0xDDDD_0001, [videoCodec]),
                ],
            },
            Certificate
        );
        sender.LocalIceCandidate += c => receiver.AddRemoteIceCandidate(c);
        receiver.LocalIceCandidate += c => sender.AddRemoteIceCandidate(c);

        // The receiver plays the browser: its offer lists transport-cc and not ack ccfb.
        SdpDescription offer = receiver.CreateOffer();
        offer = offer with
        {
            Media =
            [
                .. offer.Media.Select(static m => m with { CongestionControlFeedback = false }),
            ],
        };
        string text = SdpWriter.Write(offer);
        Assert.Contains("transport-cc", text);
        Assert.DoesNotContain("ack ccfb", text);
        Assert.IsTrue(SdpReader.TryParse(text, out SdpDescription browserOffer));
        sender.SetRemoteDescription(browserOffer, SdpType.Offer);
        SdpDescription answer = sender.CreateAnswer();
        Assert.IsTrue(answer.Media[0].TransportWideFeedback);
        Assert.IsFalse(answer.Media[0].CongestionControlFeedback);
        receiver.SetRemoteDescription(answer, SdpType.Answer);

        Assert.AreNotEqual(0, sender.NegotiatedExtensions.TransportSequence);
        byte[] payload = new byte[800];
        uint rtp = 0;
        await Cadence.SendUntilAsync(
            () => sender.TrySendRtp(96, 0xCCCC_0001, rtp += 3000, marker: true, payload),
            controller.Delivered.Task,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromSeconds(30)
        );

        Assert.IsGreaterThan(0, controller.DeliveredCount);
        Assert.IsNotNull(controller.LastRoundTrip, "transport feedback times the round trip");
    }

    // SCReAM, recording what the feedback resolved.
    private sealed class RecordingController : ICongestionController
    {
        private readonly ScreamCongestionController _inner = new();

        public TaskCompletionSource Delivered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DeliveredCount { get; private set; }

        public TimeSpan? LastRoundTrip { get; private set; }

        public CapacityEstimate Current => _inner.Current;

        public EcnCodepoint Ecn => _inner.Ecn;

        public void UseEcn(EcnCodepoint codepoint) => _inner.UseEcn(codepoint);

        public void OnPacketSent(in SentPacket packet) => _inner.OnPacketSent(packet);

        public bool CanTransmit(int size, TimeSpan now) => _inner.CanTransmit(size, now);

        public void OnMediaFrame(int bytes, TimeSpan queueDelay, TimeSpan now) =>
            _inner.OnMediaFrame(bytes, queueDelay, now);

        public CapacityEstimate OnFeedback(
            ReadOnlySpan<PacketObservation> observations,
            TimeSpan? roundTrip,
            TimeSpan now
        )
        {
            foreach (PacketObservation observation in observations)
            {
                if (
                    observation.Outcome == PacketOutcome.Delivered
                    && observation.ArrivedAt is not null
                )
                {
                    DeliveredCount++;
                }
            }

            LastRoundTrip = roundTrip ?? LastRoundTrip;
            if (DeliveredCount >= 20)
            {
                Delivered.TrySetResult();
            }

            return _inner.OnFeedback(observations, roundTrip, now);
        }

        public void OnPathChanged(TimeSpan? roundTrip, TimeSpan now) =>
            _inner.OnPathChanged(roundTrip, now);

        public CapacityEstimate OnTick(TimeSpan now) => _inner.OnTick(now);
    }
}
