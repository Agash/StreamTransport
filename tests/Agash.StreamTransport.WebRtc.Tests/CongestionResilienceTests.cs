using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.CongestionControl;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// Deterministic resilience scenarios driven entirely through the <see cref="INetworkController"/> abstraction
/// (no sockets, no timing): feed synthetic per-packet feedback and assert how the bitrate adapts and how fast
/// it recovers - loss backoff, sustained-loss collapse toward the floor, feedback-starvation easing, and
/// recovery speed after a loss spike measured in feedback intervals.
/// </summary>
[TestClass]
public sealed class CongestionResilienceTests
{
    private const long IntervalMicros = 20_000; // 20 ms feedback cadence.
    private static readonly ScreamOptions Options = new()
    {
        MinBitrateBps = 150_000,
        MaxBitrateBps = 8_000_000,
        StartBitrateBps = 600_000,
        QueueDelayTargetMs = 60,
    };

    private static PacketResult[] Clean(ref ushort seq, long now, int count = 10)
    {
        var results = new PacketResult[count];
        long sendTime = now - IntervalMicros;
        for (int i = 0; i < count; i++)
        {
            results[i] = new PacketResult(seq++, 1200, sendTime, sendTime + 10_000); // ~20 ms RTT, all received.
        }

        return results;
    }

    [TestMethod]
    public void RecoversAfterLossSpike_WithinBoundedIntervals()
    {
        var controller = new ScreamCongestionController(Options);
        long now = 0;
        ushort seq = 0;

        for (int batch = 0; batch < 100; batch++)
        {
            now += IntervalMicros;
            controller.OnFeedback(Clean(ref seq, now), now);
        }

        long peak = controller.CurrentEstimate.TargetBitrateBps;

        // A sustained loss spike (3 of 4 lost across several RTTs) drives the SCReAM v2 loss filter past its
        // threshold and forces a multiplicative back-off. Spaced > VirtualRtt (25 ms) so each report steps it.
        for (int batch = 0; batch < 5; batch++)
        {
            now += 30_000;
            controller.OnFeedback(
                [
                    new PacketResult(seq++, 1200, now - 30_000, now - 15_000),
                    new PacketResult(seq++, 1200, now - 30_000, -1),
                    new PacketResult(seq++, 1200, now - 30_000, -1),
                    new PacketResult(seq++, 1200, now - 30_000, -1),
                ],
                now);
        }

        Assert.IsTrue(controller.CurrentEstimate.TargetBitrateBps < peak, "the sustained loss spike must back the rate off.");

        // Clean delivery resumes; count feedback intervals until it climbs back to 90% of the pre-spike rate.
        int intervals = 0;
        const int max = 1000;
        while (controller.CurrentEstimate.TargetBitrateBps < peak * 0.9 && intervals < max)
        {
            now += IntervalMicros;
            controller.OnFeedback(Clean(ref seq, now), now);
            intervals++;
        }

        Assert.IsTrue(intervals < max, $"should recover to 90% of peak; gave up after {intervals} intervals.");
        // Documents the recovery speed: intervals * 20 ms.
        Assert.IsTrue(intervals * IntervalMicros / 1000 < 10_000, $"recovery took {intervals * IntervalMicros / 1000} ms (> 10 s).");
    }

    [TestMethod]
    public void SustainedLoss_CollapsesTowardFloor_ButNeverBelowIt()
    {
        var controller = new ScreamCongestionController(Options);
        long now = 0;
        ushort seq = 0;

        for (int batch = 0; batch < 100; batch++)
        {
            now += IntervalMicros;
            controller.OnFeedback(Clean(ref seq, now), now);
        }

        // Sustained ~50% loss for many batches: the rate should collapse toward the floor.
        for (int batch = 0; batch < 100; batch++)
        {
            now += IntervalMicros;
            var results = new PacketResult[10];
            long sendTime = now - IntervalMicros;
            for (int i = 0; i < results.Length; i++)
            {
                results[i] = i % 2 == 0
                    ? new PacketResult(seq++, 1200, sendTime, sendTime + 10_000)
                    : new PacketResult(seq++, 1200, sendTime, -1);
            }

            controller.OnFeedback(results, now);
        }

        Assert.IsTrue(controller.CurrentEstimate.TargetBitrateBps < Options.MaxBitrateBps / 4, "sustained loss must collapse the rate.");
        Assert.IsTrue(controller.CurrentEstimate.TargetBitrateBps >= Options.MinBitrateBps, "the rate must never drop below the floor.");
    }

    [TestMethod]
    public void FeedbackStarvation_EasesTheRateOff()
    {
        var controller = new ScreamCongestionController(Options);
        long now = 0;
        ushort seq = 0;

        for (int batch = 0; batch < 100; batch++)
        {
            now += IntervalMicros;
            controller.OnFeedback(Clean(ref seq, now), now);
        }

        long before = controller.CurrentEstimate.TargetBitrateBps;

        // No feedback for > 1 s (the link went quiet): the process tick should ease the rate down defensively.
        now += 2_000_000;
        controller.OnProcessInterval(now);

        Assert.IsTrue(controller.CurrentEstimate.TargetBitrateBps < before, "feedback starvation must ease the rate off.");
    }

    [TestMethod]
    public void Estimate_ExposesSmoothedRtt_FromFeedbackTiming()
    {
        var controller = new ScreamCongestionController(Options);
        long now = 0;
        ushort seq = 0;

        // ~40 ms RTT samples; the EWMA should converge near there.
        for (int batch = 0; batch < 50; batch++)
        {
            now += IntervalMicros;
            controller.OnFeedback([new PacketResult(seq++, 1200, now - 40_000, now - 20_000)], now);
        }

        long rttMs = controller.CurrentEstimate.SmoothedRttMicros / 1000;
        Assert.IsTrue(rttMs is > 20 and < 60, $"smoothed RTT should converge near 40 ms, got {rttMs} ms.");
        Assert.IsTrue(controller.CurrentEstimate.BaseRttMicros > 0, "base RTT should be set.");
    }
}
