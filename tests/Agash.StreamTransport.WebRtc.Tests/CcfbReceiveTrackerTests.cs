using Agash.StreamTransport.WebRtc.Rtcp;

namespace Agash.StreamTransport.WebRtc.Tests;

// RFC 8888 section 3.1 receiver rules.
[TestClass]
public sealed class CcfbReceiveTrackerTests
{
    private const uint Ssrc = 0x1234;
    private const byte Ect1 = 0x01;
    private const byte Ce = 0x03;

    [TestMethod]
    public void AddToFeedback_InOrder_ReportsEachPacketOnce()
    {
        CcfbReceiveTracker tracker = new();
        for (ushort s = 100; s < 105; s++)
        {
            tracker.OnPacket(s, s * 1000L, Ect1);
        }

        CcfbStreamReport report = Single(tracker, 110_000);
        Assert.AreEqual((ushort)100, report.BeginSequence);
        Assert.HasCount(5, report.Metrics);
        Assert.IsTrue(report.Metrics.All(static m => m.Received && m.Ecn == Ect1));

        tracker.OnPacket(105, 120_000, Ect1);
        Assert.AreEqual((ushort)105, Single(tracker, 130_000).BeginSequence, "contiguous");
    }

    // A packet reported missing that arrives later moves the next report back to it, and the packets
    // after it that were reported received are reported received again.
    [TestMethod]
    public void AddToFeedback_LateArrival_OverlapsAndKeepsReceivedPacketsReceived()
    {
        CcfbReceiveTracker tracker = new();
        tracker.OnPacket(10, 0, 0);
        tracker.OnPacket(12, 1000, 0);
        CcfbStreamReport first = Single(tracker, 2000);
        CollectionAssert.AreEqual(
            new[] { true, false, true },
            first.Metrics.Select(static m => m.Received).ToArray()
        );

        tracker.OnPacket(11, 3000, 0);
        tracker.OnPacket(13, 3000, 0);
        CcfbStreamReport second = Single(tracker, 4000);

        Assert.AreEqual((ushort)11, second.BeginSequence);
        Assert.IsTrue(second.Metrics.All(static m => m.Received));
        Assert.AreEqual(
            Ccfb.ArrivalTimeOffset(3000),
            second.Metrics[1].ArrivalTimeOffset,
            "packet 12 keeps its first arrival time"
        );
    }

    [TestMethod]
    public void OnPacket_Duplicate_KeepsTheFirstArrivalAndAnyCeMark()
    {
        CcfbReceiveTracker tracker = new();
        tracker.OnPacket(7, 0, Ect1);
        tracker.OnPacket(7, 500_000, Ce);

        CcfbMetric metric = Single(tracker, 1_000_000).Metrics[0];

        Assert.AreEqual(Ce, metric.Ecn);
        Assert.AreEqual(Ccfb.ArrivalTimeOffset(1_000_000), metric.ArrivalTimeOffset);
    }

    [TestMethod]
    public void AddToFeedback_AcrossTheSequenceWrap_IsOneRun()
    {
        CcfbReceiveTracker tracker = new();
        foreach (ushort s in new ushort[] { 65534, 65535, 0, 1 })
        {
            tracker.OnPacket(s, 0, 0);
        }

        CcfbStreamReport report = Single(tracker, 1000);
        Assert.AreEqual((ushort)65534, report.BeginSequence);
        Assert.HasCount(4, report.Metrics);
    }

    [TestMethod]
    public void AddToFeedback_LongRun_IsSplitIntoBlocks()
    {
        CcfbReceiveTracker tracker = new();
        for (int i = 0; i < 1000; i++)
        {
            tracker.OnPacket((ushort)i, 0, 0);
        }

        List<CcfbStreamReport> reports = [];
        tracker.AddToFeedback(Ssrc, 1000, 512, reports);

        Assert.HasCount(2, reports);
        Assert.HasCount(512, reports[0].Metrics);
        Assert.AreEqual((ushort)512, reports[1].BeginSequence);
        Assert.HasCount(488, reports[1].Metrics);
    }

    [TestMethod]
    public void ArrivalTimeOffset_UsesTheSentinels()
    {
        Assert.AreEqual(Ccfb.ArrivalTimeUnknown, Ccfb.ArrivalTimeOffset(-1));
        Assert.AreEqual((ushort)512, Ccfb.ArrivalTimeOffset(500_000));
        Assert.AreEqual((ushort)0x1FFD, Ccfb.ArrivalTimeOffset(8189L * 1_000_000 / 1024 + 1));
        Assert.AreEqual(Ccfb.ArrivalTimeOverRange, Ccfb.ArrivalTimeOffset(10_000_000));
    }

    // Packets the window cannot place (a sender that restarted its sequence) reset the stream.
    [TestMethod]
    public void AddToFeedback_AfterASequenceRestart_StartsOver()
    {
        CcfbReceiveTracker tracker = new();
        for (int i = 0; i < 200; i++)
        {
            tracker.OnPacket((ushort)(30_000 + i), 0, 0);
        }

        _ = Single(tracker, 1000);
        tracker.OnPacket(5, 2000, 0);
        List<CcfbStreamReport> none = [];
        tracker.AddToFeedback(Ssrc, 3000, 512, none);
        Assert.IsEmpty(none, "the restarted packet is outside the window");

        tracker.OnPacket(6, 4000, 0);
        Assert.AreEqual((ushort)6, Single(tracker, 5000).BeginSequence);
    }

    private static CcfbStreamReport Single(CcfbReceiveTracker tracker, long nowMicros)
    {
        List<CcfbStreamReport> reports = [];
        tracker.AddToFeedback(Ssrc, nowMicros, 512, reports);
        Assert.HasCount(1, reports);
        return reports[0];
    }
}
