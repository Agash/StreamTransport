namespace Agash.StreamTransport.Adaptation.Tests;

// SCReAMv2 in closed loop over a simulated path (draft-ietf-ccwg-rfc8298bis-screamv2-01).
[TestClass]
public sealed class ScreamCongestionControllerTests
{
    private static readonly ScreamOptions Options = new()
    {
        MinBitrateBps = 150_000,
        MaxBitrateBps = 20_000_000,
        StartBitrateBps = 1_000_000,
    };

    public TestContext TestContext { get; set; } = null!;

    // A clean path: the rate climbs to the capacity and the queue stays under the delay target.
    [TestMethod]
    public void CleanPath_ConvergesNearCapacityWithAShortQueue()
    {
        PathSimulator path = new(new ScreamCongestionController(Options))
        {
            CapacityBps = 5_000_000,
        };
        path.Run(TimeSpan.FromSeconds(20));
        path.Run(TimeSpan.FromSeconds(10));
        Report(path);

        Assert.IsGreaterThan(0.9 * path.CapacityBps, path.DeliveredBps);
        Assert.IsLessThan(
            30,
            path.MeanQueueDelayMs,
            "under half the 60 ms target, where delay backs off"
        );
    }

    // A capacity drop: the rate follows it down within seconds and the queue drains.
    [TestMethod]
    public void CapacityDrop_FollowsItDown()
    {
        ScreamCongestionController controller = new(Options);
        PathSimulator path = new(controller) { CapacityBps = 5_000_000 };
        path.Run(TimeSpan.FromSeconds(20));
        path.CapacityBps = 1_000_000;
        path.Run(TimeSpan.FromSeconds(3));
        path.Run(TimeSpan.FromSeconds(5));
        Report(path);

        Assert.IsLessThan(1_200_000, controller.Current.TargetBitsPerSecond);
        Assert.IsGreaterThan(0.85 * path.CapacityBps, path.DeliveredBps);
        Assert.IsLessThan(40, path.MeanQueueDelayMs);
    }

    // L4S: marking from a shallow threshold keeps the queue at a few milliseconds at full use.
    [TestMethod]
    public void L4sBottleneck_KeepsTheQueueShallowAtFullRate()
    {
        PathSimulator path = new(new ScreamCongestionController(Options))
        {
            CapacityBps = 5_000_000,
            Kind = Bottleneck.L4sMarking,
        };
        path.Run(TimeSpan.FromSeconds(20));
        path.Run(TimeSpan.FromSeconds(10));
        Report(path);

        Assert.IsGreaterThan(0, path.Marked);
        Assert.IsGreaterThan(0.85 * path.CapacityBps, path.DeliveredBps);
        Assert.IsLessThan(5, path.MeanQueueDelayMs);
    }

    // RFC 9331 section 4.3: CE marks with a standing queue behind them come from a classic AQM, which the
    // controller answers classically, marking ECT(0); an L4S AQM keeps the scalable response.
    [TestMethod]
    [DataRow(Bottleneck.ClassicMarking, EcnMode.Classic, EcnCodepoint.Ect0)]
    [DataRow(Bottleneck.L4sMarking, EcnMode.L4s, EcnCodepoint.Ect1)]
    public void EcnBottleneck_IsAnsweredAsItMarks(
        Bottleneck kind,
        EcnMode expected,
        EcnCodepoint codepoint
    )
    {
        ScreamCongestionController controller = new(Options);
        PathSimulator path = new(controller) { CapacityBps = 5_000_000, Kind = kind };
        path.Run(TimeSpan.FromSeconds(20));

        Assert.IsGreaterThan(0, path.Marked);
        Assert.AreEqual(expected, controller.EcnMode);
        Assert.AreEqual(codepoint, controller.Ecn);
    }

    // Section 4.5.2: random link-layer loss below the threshold, with no queue, does not collapse the rate.
    [TestMethod]
    public void LinkLayerLossBelowTheThreshold_KeepsTheRate()
    {
        PathSimulator path = new(new ScreamCongestionController(Options))
        {
            CapacityBps = 5_000_000,
            RandomLoss = 0.003,
        };
        path.Run(TimeSpan.FromSeconds(20));
        path.Run(TimeSpan.FromSeconds(10));
        Report(path);

        Assert.IsGreaterThan(0, path.Lost);
        Assert.IsGreaterThan(0.85 * path.CapacityBps, path.DeliveredBps);
    }

    // Section 4.5.2: a policer drops above its rate with no queue; the window settles under it.
    [TestMethod]
    public void Policer_SettlesUnderItsRate()
    {
        PathSimulator path = new(new ScreamCongestionController(Options))
        {
            CapacityBps = 3_000_000,
            Kind = Bottleneck.Policer,
        };
        path.Run(TimeSpan.FromSeconds(20));
        int lostBefore = path.Lost;
        path.Run(TimeSpan.FromSeconds(10));
        Report(path);

        Assert.IsGreaterThan(0.7 * path.CapacityBps, path.DeliveredBps);
        Assert.IsLessThan(
            0.05 * path.DeliveredBps * 10 / 8 / 1200,
            path.Lost - lostBefore,
            "under 5 % of packets lost once settled"
        );
    }

    // No arrival times (a QUIC-like transport): the round trip above its floor stands in for queue delay.
    [TestMethod]
    public void WithoutArrivalTimes_StillHoldsTheQueueDown()
    {
        PathSimulator path = new(new ScreamCongestionController(Options))
        {
            CapacityBps = 5_000_000,
            ReportArrivalTimes = false,
        };
        path.Run(TimeSpan.FromSeconds(20));
        path.Run(TimeSpan.FromSeconds(10));
        Report(path);

        Assert.IsGreaterThan(0.85 * path.CapacityBps, path.DeliveredBps);
        Assert.IsLessThan(40, path.MeanQueueDelayMs);
    }

    // Section 4.2.1: two congestion signals within one round trip reduce the window once.
    [TestMethod]
    public void CongestionTwiceWithinARoundTrip_ReducesOnce()
    {
        ScreamCongestionController controller = new(new ScreamOptions { Ecn = EcnMode.Classic });
        var now = TimeSpan.FromSeconds(1);
        Warm(controller, ref now);
        double before = controller.ReferenceWindow;

        _ = controller.OnFeedback([Marked(1000, now)], TimeSpan.FromMilliseconds(100), now);
        double once = controller.ReferenceWindow;
        now += TimeSpan.FromMilliseconds(10);
        _ = controller.OnFeedback([Marked(1001, now)], TimeSpan.FromMilliseconds(100), now);

        Assert.AreEqual(before * 0.8, once, before * 0.02, "classic ECN scales by BETA_ECN");
        Assert.AreEqual(
            once,
            controller.ReferenceWindow,
            "within the round trip, no second reduction"
        );
    }

    // Section 4.3.1 and the reference implementation: the window closes when the bytes in flight fill it,
    // and reopens after half a second without a send, so lost feedback cannot deadlock the sender.
    [TestMethod]
    public void SendWindow_ClosesWhenFullAndReleasesAfterHalfASecond()
    {
        ScreamCongestionController controller = new(Options);
        var now = TimeSpan.FromSeconds(1);
        long id = 0;
        while (controller.CanTransmit(1200, now))
        {
            controller.OnPacketSent(new SentPacket(++id, 1200, now, TrafficClass.Video));
        }

        Assert.IsFalse(controller.CanTransmit(1200, now + TimeSpan.FromMilliseconds(400)));
        Assert.IsTrue(controller.CanTransmit(1200, now + TimeSpan.FromMilliseconds(600)));
    }

    // A new path: every estimate is back where a new controller starts, the round trip seeded from the one
    // measured on the new path, and the controller converges on the new path's capacity from there.
    [TestMethod]
    public void OnPathChanged_StartsOverAsANewController_AndConvergesOnTheNewPath()
    {
        ScreamCongestionController controller = new(Options);
        PathSimulator wifi = new(controller) { CapacityBps = 8_000_000 };
        wifi.Run(TimeSpan.FromSeconds(20));
        Assert.IsGreaterThan(4_000_000, controller.Current.TargetBitsPerSecond);

        controller.OnPathChanged(TimeSpan.FromMilliseconds(60), TimeSpan.FromSeconds(20));

        ScreamCongestionController fresh = new(Options);
        Assert.AreEqual(fresh.Current.TargetBitsPerSecond, controller.Current.TargetBitsPerSecond);
        Assert.AreEqual(fresh.ReferenceWindow, controller.ReferenceWindow);
        Assert.AreEqual(0, controller.BytesInFlight);
        Assert.AreEqual(TimeSpan.FromMilliseconds(60), controller.Current.SmoothedRoundTrip);

        PathSimulator cellular = new(controller) { CapacityBps = 3_000_000 };
        cellular.Run(TimeSpan.FromSeconds(20));
        cellular.Run(TimeSpan.FromSeconds(10));
        Report(cellular);
        Assert.IsGreaterThan(0.85 * cellular.CapacityBps, cellular.DeliveredBps);
        Assert.IsLessThan(30, cellular.MeanQueueDelayMs);
    }

    private static void Warm(ScreamCongestionController controller, ref TimeSpan now)
    {
        for (int i = 0; i < 50; i++)
        {
            now += TimeSpan.FromMilliseconds(20);
            _ = controller.OnFeedback(
                [
                    new PacketObservation(
                        new SentPacket(i, 1200, now, TrafficClass.Video),
                        PacketOutcome.Delivered,
                        now,
                        EcnCodepoint.Ect0
                    ),
                ],
                TimeSpan.FromMilliseconds(100),
                now
            );
        }

        now += TimeSpan.FromMilliseconds(200);
    }

    private static PacketObservation Marked(long id, TimeSpan now) =>
        new(
            new SentPacket(id, 1200, now, TrafficClass.Video),
            PacketOutcome.Delivered,
            now,
            EcnCodepoint.Ce
        );

    private void Report(PathSimulator path) =>
        TestContext.WriteLine(
            $"delivered {path.DeliveredBps / 1000} kbit/s of {path.CapacityBps / 1000}, queue mean {path.MeanQueueDelayMs:F1} ms max {path.MaxQueueDelayMs:F1} ms, lost {path.Lost}, marked {path.Marked}"
        );
}
