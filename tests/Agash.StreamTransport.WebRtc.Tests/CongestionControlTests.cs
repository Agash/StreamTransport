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
    public void Scream_Loss_ReducesTarget()
    {
        var controller = new ScreamCongestionController(Options);
        long now = 0;
        ushort seq = 0;

        // Ramp up first.
        for (int batch = 0; batch < 50; batch++)
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

        long beforeLoss = controller.CurrentEstimate.TargetBitrateBps;

        // A batch with losses (ReceiveTime -1).
        now += 20_000;
        PacketResult[] lossy =
        [
            new PacketResult(seq++, 1200, now - 20_000, now - 10_000),
            new PacketResult(seq++, 1200, now - 20_000, -1),
            new PacketResult(seq++, 1200, now - 20_000, -1),
        ];
        controller.OnFeedback(lossy, now);

        Assert.IsTrue(controller.CurrentEstimate.TargetBitrateBps < beforeLoss, "loss must reduce the target");
        Assert.IsTrue(controller.CurrentEstimate.TargetBitrateBps >= Options.MinBitrateBps);
    }

    [TestMethod]
    public void Scream_EcnCe_ReducesTarget_GentlerThanLoss()
    {
        // RFC 9331 / SCReAM v2: an ECN-CE mark signals congestion before loss, and the reaction (cwnd *= 1 -
        // alpha/2) is deliberately gentler than the loss multiplicative back-off. Two identically-ramped
        // controllers each take a single congestion feedback - one all-CE, one with a loss - and the CE one must
        // end up higher.
        var ceController = new ScreamCongestionController(Options);
        var lossController = new ScreamCongestionController(Options);
        (long ceNow, ushort ceSeq) = RampClean(ceController, 50);
        (long lossNow, ushort lossSeq) = RampClean(lossController, 50);

        long before = ceController.CurrentEstimate.TargetBitrateBps;
        Assert.AreEqual(before, lossController.CurrentEstimate.TargetBitrateBps, "identical ramps should match");

        ceNow += 20_000;
        var ce = new PacketResult[10];
        for (int i = 0; i < ce.Length; i++)
        {
            long sendTime = ceNow - 20_000;
            ce[i] = new PacketResult(ceSeq++, 1200, sendTime, sendTime + 10_000, Ecn: 0x03);
        }
        ceController.OnFeedback(ce, ceNow);

        lossNow += 20_000;
        var loss = new PacketResult[10];
        for (int i = 0; i < loss.Length; i++)
        {
            long sendTime = lossNow - 20_000;
            loss[i] = new PacketResult(lossSeq++, 1200, sendTime, i == 0 ? -1 : sendTime + 10_000);
        }
        lossController.OnFeedback(loss, lossNow);

        long afterCe = ceController.CurrentEstimate.TargetBitrateBps;
        long afterLoss = lossController.CurrentEstimate.TargetBitrateBps;

        Assert.IsTrue(afterCe < before, "ECN-CE must reduce the target");
        Assert.IsTrue(afterLoss < before, "loss must reduce the target");
        Assert.IsTrue(afterCe > afterLoss, "a single ECN-CE batch must back off gentler than loss (RFC 9331 L4S)");
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
