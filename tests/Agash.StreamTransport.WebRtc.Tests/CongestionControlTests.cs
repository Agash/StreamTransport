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
            now += 300_000; // 300 ms RTT — far above the 60 ms target
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
