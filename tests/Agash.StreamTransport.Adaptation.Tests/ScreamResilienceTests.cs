using static Agash.StreamTransport.Adaptation.Tests.Feedback;

namespace Agash.StreamTransport.Adaptation.Tests;

/// <summary>
/// Deterministic resilience scenarios driven entirely through the <see cref="ICongestionController"/> abstraction
/// (no sockets, no timing): feed synthetic per-packet feedback and assert how the bitrate adapts and how fast
/// it recovers - loss backoff, sustained-loss collapse toward the floor, feedback-starvation easing, and
/// recovery speed after a loss spike measured in feedback intervals.
/// </summary>
[TestClass]
public sealed class ScreamResilienceTests
{
    private const long IntervalMicros = 20_000; // 20 ms feedback cadence.
    private static readonly ScreamOptions Options = new()
    {
        MinBitrateBps = 150_000,
        MaxBitrateBps = 8_000_000,
        StartBitrateBps = 600_000,
        QueueDelayTargetMs = 60,
    };

    private static Sent[] Clean(ref ushort seq, long now, int count = 10)
    {
        var results = new Sent[count];
        long sendTime = now - IntervalMicros;
        for (int i = 0; i < count; i++)
        {
            results[i] = Packet(seq++, 1200, sendTime, sendTime + 10_000); // ~20 ms RTT, all received.
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
            Feed(controller, Clean(ref seq, now), now);
        }

        long peak = controller.Current.TargetBitsPerSecond;

        // A sustained loss spike (3 of 4 lost across several RTTs) drives the SCReAM v2 loss filter past its
        // threshold and forces a multiplicative back-off. Spaced > VirtualRtt (25 ms) so each report steps it.
        for (int batch = 0; batch < 5; batch++)
        {
            now += 30_000;
            Feed(
                controller,
                [
                    Packet(seq++, 1200, now - 30_000, now - 15_000),
                    Packet(seq++, 1200, now - 30_000, -1),
                    Packet(seq++, 1200, now - 30_000, -1),
                    Packet(seq++, 1200, now - 30_000, -1),
                ],
                now
            );
        }

        Assert.IsTrue(
            controller.Current.TargetBitsPerSecond < peak,
            "the sustained loss spike must back the rate off."
        );

        // Clean delivery resumes; count feedback intervals until it climbs back to 90% of the pre-spike rate.
        int intervals = 0;
        const int max = 1000;
        while (controller.Current.TargetBitsPerSecond < peak * 0.9 && intervals < max)
        {
            now += IntervalMicros;
            Feed(controller, Clean(ref seq, now), now);
            intervals++;
        }

        Assert.IsTrue(
            intervals < max,
            $"should recover to 90% of peak; gave up after {intervals} intervals."
        );
        // Documents the recovery speed: intervals * 20 ms.
        Assert.IsTrue(
            intervals * IntervalMicros / 1000 < 10_000,
            $"recovery took {intervals * IntervalMicros / 1000} ms (> 10 s)."
        );
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
            Feed(controller, Clean(ref seq, now), now);
        }

        // Sustained ~50% loss for many batches: the rate should collapse toward the floor.
        for (int batch = 0; batch < 100; batch++)
        {
            now += IntervalMicros;
            var results = new Sent[10];
            long sendTime = now - IntervalMicros;
            for (int i = 0; i < results.Length; i++)
            {
                results[i] =
                    i % 2 == 0
                        ? Packet(seq++, 1200, sendTime, sendTime + 10_000)
                        : Packet(seq++, 1200, sendTime, -1);
            }

            Feed(controller, results, now);
        }

        Assert.IsTrue(
            controller.Current.TargetBitsPerSecond < Options.MaxBitrateBps / 4,
            "sustained loss must collapse the rate."
        );
        Assert.IsTrue(
            controller.Current.TargetBitsPerSecond >= Options.MinBitrateBps,
            "the rate must never drop below the floor."
        );
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
            Feed(controller, Clean(ref seq, now), now);
        }

        long before = controller.Current.TargetBitsPerSecond;

        // No feedback for > 1 s (the link went quiet): the process tick should ease the rate down defensively.
        now += 2_000_000;
        controller.OnTick(Time(now));

        Assert.IsTrue(
            controller.Current.TargetBitsPerSecond < before,
            "feedback starvation must ease the rate off."
        );
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
            Feed(controller, [Packet(seq++, 1200, now - 40_000, now - 20_000)], now);
        }

        long rttMs = (long)controller.Current.SmoothedRoundTrip.TotalMilliseconds;
        Assert.IsTrue(
            rttMs is > 20 and < 60,
            $"smoothed RTT should converge near 40 ms, got {rttMs} ms."
        );
        Assert.IsTrue(
            controller.Current.MinimumRoundTrip > TimeSpan.Zero,
            "base RTT should be set."
        );
    }
}
