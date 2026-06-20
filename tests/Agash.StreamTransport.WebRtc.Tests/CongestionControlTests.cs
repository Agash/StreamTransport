using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.CongestionControl;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class CongestionControlTests
{
    private static readonly ScreamOptions Options = new()
    {
        MinBitrateBps = 150_000,
        MaxBitrateBps = 8_000_000,
        StartBitrateBps = 600_000,
        QueueDelayTargetMs = 60,
    };

    [TestMethod]
    public void Scream_CleanLowDelayDelivery_RampsTowardMax()
    {
        var controller = new ScreamCongestionController(Options);
        long now = 0;
        ushort seq = 0;
        long start = controller.CurrentEstimate.TargetBitrateBps;

        // 200 feedback batches of fully-received packets at a steady low RTT (~20 ms).
        for (int batch = 0; batch < 200; batch++)
        {
            now += 20_000;
            var results = new PacketResult[10];
            for (int i = 0; i < results.Length; i++)
            {
                long sendTime = now - 20_000;
                results[i] = new PacketResult(seq++, 1200, sendTime, sendTime + 10_000);
            }

            controller.OnFeedback(results, now);
        }

        Assert.IsTrue(controller.CurrentEstimate.TargetBitrateBps > start, "rate should grow on clean delivery");
        Assert.IsTrue(controller.CurrentEstimate.PacingRateBps > controller.CurrentEstimate.TargetBitrateBps, "pacing has headroom");
    }

    [TestMethod]
    public void Scream_SustainedLoss_ReducesTarget()
    {
        // SCReAM v2: loss must persist across several RTTs (the asymmetric filter reaching its threshold) before
        // the controller backs off. Feedback is spaced > VirtualRtt (25 ms) so each report steps the filter.
        var controller = new ScreamCongestionController(Options);
        (long now, ushort seq) = RampClean(controller, 50);
        long beforeLoss = controller.CurrentEstimate.TargetBitrateBps;

        for (int batch = 0; batch < 10; batch++)
        {
            now += 30_000;
            PacketResult[] lossy =
            [
                new PacketResult(seq++, 1200, now - 30_000, now - 15_000),
                new PacketResult(seq++, 1200, now - 30_000, -1),
                new PacketResult(seq++, 1200, now - 30_000, -1),
            ];
            controller.OnFeedback(lossy, now);
        }

        Assert.IsTrue(controller.CurrentEstimate.TargetBitrateBps < beforeLoss, "sustained loss must reduce the target");
        Assert.IsTrue(controller.CurrentEstimate.TargetBitrateBps >= Options.MinBitrateBps);
    }

    [TestMethod]
    public void Scream_SpuriousLoss_DoesNotCollapseTarget()
    {
        // A flaky wireless link: ~1 lost packet in most reports, but never sustained. The asymmetric loss filter
        // drifts net-negative under this noise, so the controller must NOT collapse the rate - it holds or grows.
        var controller = new ScreamCongestionController(Options);
        (long now, ushort seq) = RampClean(controller, 50);
        long beforeNoise = controller.CurrentEstimate.TargetBitrateBps;

        for (int batch = 0; batch < 100; batch++)
        {
            // Space reports 30 ms apart (> VirtualRtt, so each steps the filter) but keep the RTT sample at the
            // same 20 ms as the ramp, so any rate change comes from the loss filter, not an RTT shift.
            now += 30_000;
            var results = new PacketResult[10];
            for (int i = 0; i < results.Length; i++)
            {
                long sendTime = now - 20_000;
                // One spurious loss every third report; the rest delivered cleanly at a low RTT.
                bool lost = batch % 3 == 0 && i == 0;
                results[i] = new PacketResult(seq++, 1200, sendTime, lost ? -1 : sendTime + 10_000);
            }

            controller.OnFeedback(results, now);
        }

        Assert.IsTrue(controller.CurrentEstimate.TargetBitrateBps >= beforeNoise,
            "spurious random loss must not drive a back-off (the rate should hold or keep growing)");
    }

    [TestMethod]
    public void Scream_SingleEcnCe_BacksOffGentlerThanLossFactor()
    {
        // RFC 9331 / SCReAM v2: an ECN-CE mark signals congestion before loss, and a single all-CE feedback
        // reaction (cwnd *= 1 - alpha/2, with a fast-attack alpha) is deliberately gentler than a full loss
        // multiplicative back-off (cwnd *= BackoffFactor). Unlike loss, an ECN-CE mark reacts immediately - it is
        // an explicit congestion signal, not noise to be filtered.
        var controller = new ScreamCongestionController(Options);
        (long now, ushort seq) = RampClean(controller, 50);
        long before = controller.CurrentEstimate.TargetBitrateBps;

        now += 20_000;
        var ce = new PacketResult[10];
        for (int i = 0; i < ce.Length; i++)
        {
            long sendTime = now - 20_000;
            ce[i] = new PacketResult(seq++, 1200, sendTime, sendTime + 10_000, Ecn: 0x03);
        }
        controller.OnFeedback(ce, now);

        long afterCe = controller.CurrentEstimate.TargetBitrateBps;
        Assert.IsTrue(afterCe < before, "ECN-CE must reduce the target");
        Assert.IsTrue(afterCe > before * Options.BackoffFactor,
            "a single ECN-CE batch must back off gentler than a full loss back-off (RFC 9331 L4S)");
    }

    [TestMethod]
    public void Scream_SustainedEcnCe_DrivesProgressiveBackoff()
    {
        // The L4S alpha is a fast-attack EWMA of the CE-marked fraction; under sustained full marking it ramps
        // toward 1, so the per-feedback back-off grows toward ~50% and the rate collapses to the floor (but never
        // below it).
        var controller = new ScreamCongestionController(Options);
        (long now, ushort seq) = RampClean(controller, 80);
        long before = controller.CurrentEstimate.TargetBitrateBps;

        for (int batch = 0; batch < 40; batch++)
        {
            now += 20_000;
            var ce = new PacketResult[10];
            for (int i = 0; i < ce.Length; i++)
            {
                long sendTime = now - 20_000;
                ce[i] = new PacketResult(seq++, 1200, sendTime, sendTime + 10_000, Ecn: 0x03);
            }

            controller.OnFeedback(ce, now);
        }

        long after = controller.CurrentEstimate.TargetBitrateBps;
        Assert.IsTrue(after < before / 2, "sustained ECN-CE must drive the rate well below half");
        Assert.IsTrue(after >= Options.MinBitrateBps, "but never below the configured floor");
    }

    private static (long Now, ushort Seq) RampClean(ScreamCongestionController controller, int batches)
    {
        long now = 0;
        ushort seq = 0;
        for (int batch = 0; batch < batches; batch++)
        {
            now += 20_000;
            var ok = new PacketResult[10];
            for (int i = 0; i < ok.Length; i++)
            {
                long sendTime = now - 20_000;
                ok[i] = new PacketResult(seq++, 1200, sendTime, sendTime + 10_000);
            }

            controller.OnFeedback(ok, now);
        }

        return (now, seq);
    }

    [TestMethod]
    public void Scream_StandingQueueDelay_StopsGrowth()
    {
        var controller = new ScreamCongestionController(Options);
        long now = 0;
        ushort seq = 0;

        // Establish a low base RTT.
        now += 20_000;
        controller.OnFeedback([new PacketResult(seq++, 1200, now - 20_000, now - 10_000)], now);

        // Now RTT balloons well past the queue-delay target (bufferbloat): growth must stall / back off.
        long high = controller.CurrentEstimate.TargetBitrateBps;
        for (int batch = 0; batch < 20; batch++)
        {
            now += 300_000; // 300 ms RTT - far above the 60 ms target
            controller.OnFeedback([new PacketResult(seq++, 1200, now - 300_000, now - 150_000)], now);
        }

        Assert.IsTrue(controller.CurrentEstimate.TargetBitrateBps <= high, "standing queue delay must not let the rate grow");
    }

    [TestMethod]
    public void Scream_AlternatingLossRtts_DoNotReachBackoff()
    {
        // The asymmetric filter's whole point: even when ~50% of RTTs carry a loss but they never run
        // consecutively, the net drift stays negative and the rate is never backed off. (One loss RTT: +1/3;
        // one lossless RTT: -1/2; alternating nets -1/6 per pair.)
        var controller = new ScreamCongestionController(Options);
        (long now, ushort seq) = RampClean(controller, 50);
        long before = controller.CurrentEstimate.TargetBitrateBps;

        for (int batch = 0; batch < 60; batch++)
        {
            now += 30_000; // > VirtualRtt so every report steps the filter; RTT sample held at 20 ms
            bool lossRtt = batch % 2 == 0;
            var results = new PacketResult[10];
            for (int i = 0; i < results.Length; i++)
            {
                long sendTime = now - 20_000;
                bool lost = lossRtt && i == 0;
                results[i] = new PacketResult(seq++, 1200, sendTime, lost ? -1 : sendTime + 10_000);
            }

            controller.OnFeedback(results, now);
        }

        Assert.IsTrue(controller.CurrentEstimate.TargetBitrateBps >= before,
            "alternating (non-consecutive) loss RTTs must not trigger a back-off");
    }

    [TestMethod]
    public void PacingBudget_RefillsAtRate_AndCapsBurst()
    {
        var budget = new PacingBudget(initialRateBps: 8_000_000, maxBurstMs: 40); // 1 MB/s
        budget.Refill(0); // prime

        // After 10 ms at 1 MB/s, ~10 000 bytes available.
        int available = budget.Refill(10_000);
        Assert.IsTrue(available is > 9_000 and < 11_000, $"expected ~10 000 bytes, got {available}");

        // A long idle does not let the budget exceed the burst cap (~40 ms = ~40 000 bytes).
        int capped = budget.Refill(10_000 + 5_000_000);
        Assert.IsTrue(capped <= 41_000, $"burst must be capped, got {capped}");

        budget.Consume(capped);
        Assert.AreEqual(0, budget.Refill(10_000 + 5_000_000));
    }
}
