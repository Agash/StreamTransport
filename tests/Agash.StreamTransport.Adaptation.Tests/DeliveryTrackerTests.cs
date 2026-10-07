namespace Agash.StreamTransport.Adaptation.Tests;

[TestClass]
public sealed class DeliveryTrackerTests
{
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    [TestMethod]
    public void OnFeedback_Received_IsDeliveredOnce()
    {
        DeliveryTracker tracker = Sent(3);
        List<PacketObservation> first = [];
        _ = tracker.OnFeedback([Got(1), Got(2)], 30 * Ms, first);

        // An overlapping report covers 2 again (RFC 8888 section 3.1): it is not counted twice.
        List<PacketObservation> second = [];
        _ = tracker.OnFeedback([Got(2), Got(3)], 40 * Ms, second);

        Assert.HasCount(2, first);
        Assert.HasCount(1, second);
        Assert.AreEqual(3, second[0].Packet.Id);
    }

    // A gap is not a loss until a later packet arrived and the reordering window passed.
    [TestMethod]
    public void OnFeedback_MissingBehindALaterPacket_IsLost()
    {
        DeliveryTracker tracker = Sent(3);
        List<PacketObservation> seen = [];
        _ = tracker.OnFeedback([Got(1), Missing(2), Got(3)], 30 * Ms, seen);

        Assert.AreEqual(
            PacketOutcome.Lost,
            seen.Single(static o => o.Packet.Id == 2).Outcome,
            "with no reordering seen yet the window is zero"
        );
    }

    [TestMethod]
    public void OnFeedback_MissingWithNothingLaterReceived_IsNotYetLost()
    {
        DeliveryTracker tracker = Sent(3);
        List<PacketObservation> seen = [];
        _ = tracker.OnFeedback([Got(1), Missing(2), Missing(3)], 30 * Ms, seen);

        Assert.HasCount(1, seen);
    }

    // SCReAMv2 section 4.1.2: a packet declared lost that arrives was reordered; the window grows to the
    // delay it showed, and the next gap of that size is waited out.
    [TestMethod]
    public void OnFeedback_LostPacketArriving_IsDeliveredLateAndWidensTheWindow()
    {
        DeliveryTracker tracker = Sent(6);
        List<PacketObservation> seen = [];
        _ = tracker.OnFeedback([Got(1), Missing(2), Got(3)], 30 * Ms, seen);
        seen.Clear();
        _ = tracker.OnFeedback([Got(2)], 45 * Ms, seen);

        Assert.AreEqual(PacketOutcome.DeliveredLate, seen.Single().Outcome);
        Assert.AreEqual(15, tracker.ReorderingWindow.TotalMilliseconds, 1);

        seen.Clear();
        _ = tracker.OnFeedback([Missing(4), Got(5)], 50 * Ms, seen);
        Assert.IsFalse(
            seen.Exists(static o => o.Outcome == PacketOutcome.Lost),
            "inside the window"
        );

        seen.Clear();
        tracker.OnTick(70 * Ms, seen);
        Assert.AreEqual(4, seen.Single(static o => o.Outcome == PacketOutcome.Lost).Packet.Id);
    }

    // RFC 8888: a received packet whose arrival time is unknown or over range is still received, and its
    // feedback still times the round trip, which needs only the sending and feedback times.
    [TestMethod]
    public void OnFeedback_ReceivedWithoutATime_IsDeliveredAndTimesTheRoundTrip()
    {
        DeliveryTracker tracker = Sent(1);
        List<PacketObservation> seen = [];
        TimeSpan? roundTrip = tracker.OnFeedback(
            [new PacketReport(1, true, null, EcnCodepoint.Ect1)],
            30 * Ms,
            seen
        );

        Assert.AreEqual(PacketOutcome.Delivered, seen.Single().Outcome);
        Assert.AreEqual(EcnCodepoint.Ect1, seen.Single().Ecn);
        Assert.AreEqual(29 * Ms, roundTrip, "sent at 1 ms, feedback at 30 ms");
    }

    // The round trip runs from sending the latest-sent packet until its feedback arrives, the receiver's
    // wait included, as SCReAMv2's s_rtt does.
    [TestMethod]
    public void OnFeedback_RoundTrip_RunsFromSendingUntilTheFeedbackArrives()
    {
        DeliveryTracker tracker = new();
        tracker.OnSent(new SentPacket(1, 1200, 10 * Ms, TrafficClass.Video));
        tracker.OnSent(new SentPacket(2, 1200, 20 * Ms, TrafficClass.Video));
        List<PacketObservation> seen = [];

        TimeSpan? roundTrip = tracker.OnFeedback(
            [
                new PacketReport(1, true, null, EcnCodepoint.NotEct),
                new PacketReport(2, true, null, EcnCodepoint.NotEct),
            ],
            90 * Ms,
            seen
        );

        Assert.AreEqual(70 * Ms, roundTrip, "from the latest-sent packet: 90 - 20");
    }

    [TestMethod]
    public void OnSent_ForgetsPacketsOlderThanTheHistory()
    {
        DeliveryTracker tracker = new(TimeSpan.FromSeconds(1));
        for (int i = 0; i < 30; i++)
        {
            tracker.OnSent(new SentPacket(i, 1000, i * 100 * Ms, TrafficClass.Video));
        }

        Assert.AreEqual(11, tracker.Count, "the last second of sends, at 100 ms apart");
        List<PacketObservation> seen = [];
        _ = tracker.OnFeedback([Got(0)], 3000 * Ms, seen);
        Assert.IsEmpty(seen);
    }

    // RFC 8985 section 6.2: a packet no report mentions, sent before one that was delivered, is lost once
    // RACK.rtt and the reordering window have passed since it was sent.
    [TestMethod]
    public void UnreportedPacket_SentBeforeADeliveredOne_IsLostOnceDue()
    {
        DeliveryTracker tracker = new();
        tracker.OnSent(new SentPacket(1, 1200, 5 * Ms, TrafficClass.Video));
        tracker.OnSent(new SentPacket(2, 1200, 10 * Ms, TrafficClass.Video));
        List<PacketObservation> seen = [];

        // Packet 2 is reported at 60 ms: RACK.rtt and the round trip are 50 ms, and the window a quarter of
        // that, so packet 1 is due at 5 + 50 + 12.5 = 67.5 ms.
        _ = tracker.OnFeedback(
            [new PacketReport(2, true, null, EcnCodepoint.NotEct)],
            60 * Ms,
            seen
        );
        Assert.HasCount(1, seen);
        tracker.OnTick(67 * Ms, seen);
        Assert.HasCount(1, seen, "not due before RACK.rtt and the window");

        tracker.OnTick(68 * Ms, seen);
        Assert.HasCount(2, seen);
        Assert.AreEqual(1, seen[1].Packet.Id);
        Assert.AreEqual(PacketOutcome.Lost, seen[1].Outcome);
    }

    [TestMethod]
    public void UnreportedPacket_SentAfterTheLatestDelivered_IsNotLost()
    {
        DeliveryTracker tracker = new();
        tracker.OnSent(new SentPacket(1, 1200, 0 * Ms, TrafficClass.Video));
        tracker.OnSent(new SentPacket(2, 1200, 10 * Ms, TrafficClass.Video));
        List<PacketObservation> seen = [];

        _ = tracker.OnFeedback([Got(1)], 40 * Ms, seen);
        tracker.OnTick(1000 * Ms, seen);

        Assert.HasCount(1, seen);
        Assert.AreEqual(1, seen[0].Packet.Id);
    }

    [TestMethod]
    public void UnreportedPacketDeclaredLost_ThatArrivesAfterAll_IsDeliveredLate()
    {
        DeliveryTracker tracker = new();
        tracker.OnSent(new SentPacket(1, 1200, 0 * Ms, TrafficClass.Video));
        tracker.OnSent(new SentPacket(2, 1200, 10 * Ms, TrafficClass.Video));
        List<PacketObservation> seen = [];
        _ = tracker.OnFeedback([Got(2)], 40 * Ms, seen);
        tracker.OnTick(200 * Ms, seen);

        _ = tracker.OnFeedback([Got(1)], 210 * Ms, seen);

        Assert.AreEqual(PacketOutcome.Lost, seen[1].Outcome);
        Assert.AreEqual(PacketOutcome.DeliveredLate, seen[2].Outcome);
    }

    [TestMethod]
    public void OnPathChanged_FeedbackAboutTheOldPathResolvesNothing()
    {
        DeliveryTracker tracker = Sent(3);
        tracker.OnPathChanged();
        tracker.OnSent(new SentPacket(4, 1200, 10 * Ms, TrafficClass.Video));
        List<PacketObservation> observations = [];

        TimeSpan? roundTrip = tracker.OnFeedback(
            [Got(1), Missing(2), Got(3), Got(4)],
            30 * Ms,
            observations
        );
        tracker.OnTick(TimeSpan.FromSeconds(1), observations);

        Assert.AreEqual(
            20 * Ms,
            roundTrip,
            "the round trip comes from the new path's packet alone."
        );
        Assert.HasCount(1, observations);
        Assert.AreEqual(4, observations[0].Packet.Id);
        Assert.AreEqual(1, tracker.Count);
    }

    private static DeliveryTracker Sent(int count)
    {
        DeliveryTracker tracker = new();
        for (int id = 1; id <= count; id++)
        {
            tracker.OnSent(new SentPacket(id, 1200, id * Ms, TrafficClass.Video));
        }

        return tracker;
    }

    private static PacketReport Got(long id) => new(id, true, null, EcnCodepoint.NotEct);

    private static PacketReport Missing(long id) => new(id, false, null, EcnCodepoint.NotEct);
}
