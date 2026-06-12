using System.Collections.Concurrent;
using Agash.StreamTransport.Sync;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// Deterministic tests for the playout scheduler, driven by an injected fake wall clock (no real
/// timing dependence beyond bounded waits). Proves frames release in release-time order and that a frame is
/// held until its scheduled time, plus the pure <see cref="PlayoutTimeline"/> mapping.
/// </summary>
[TestClass]
public sealed class PlayoutSchedulerTests
{
    [TestMethod]
    public void Timeline_MapsCorrelatedInstantsToSameRelease()
    {
        var timeline = new PlayoutTimeline(minDelayNs: 200_000_000, maxDelayNs: 200_000_000, marginNs: 0);
        Assert.IsFalse(timeline.IsAnchored);

        // First frame establishes the offset: sender 5_000 ms, arrives local 6_000 ms -> offset 1_000 ms.
        long rA = timeline.ReleaseLocalNs(senderWallNs: 5_000_000_000, localNowNs: 6_000_000_000);
        Assert.IsTrue(timeline.IsAnchored);
        Assert.AreEqual(5_000_000_000 + 1_000_000_000 + 200_000_000, rA);

        // A co-captured frame on the other stream (same sender wall), arriving a little later, gets the same
        // release within the leak tolerance - so audio and video present together (lip-sync).
        long rB = timeline.ReleaseLocalNs(senderWallNs: 5_000_000_000, localNowNs: 6_030_000_000);
        Assert.AreEqual(rA, rB, delta: 1_000_000); // within 1 ms (leak over 30 ms is ~6 us)
    }

    [TestMethod]
    public void Timeline_RejectsLatencyTransientsButFollowsALowerPath()
    {
        var timeline = new PlayoutTimeline(minDelayNs: 200_000_000, maxDelayNs: 200_000_000, marginNs: 0);

        // Steady offset ~1_000 ms (capture 5_000 ms -> arrive 6_000 ms).
        timeline.ReleaseLocalNs(senderWallNs: 5_000_000_000, localNowNs: 6_000_000_000);

        // A transient: a frame that arrives 200 ms late (jitter spike / cold start) must NOT push the offset up,
        // so a normal frame right after still maps on the minimum offset (~1_000 ms), not the inflated one.
        timeline.ReleaseLocalNs(senderWallNs: 5_100_000_000, localNowNs: 6_300_000_000); // 200 ms late
        long rNormal = timeline.ReleaseLocalNs(senderWallNs: 5_200_000_000, localNowNs: 6_200_000_000);
        Assert.AreEqual(5_200_000_000 + 1_000_000_000 + 200_000_000, rNormal, delta: 1_000_000);

        // A genuinely lower-latency path is adopted immediately (offset drops to it).
        timeline.ReleaseLocalNs(senderWallNs: 5_300_000_000, localNowNs: 6_240_000_000); // offset now ~940 ms
        long rLower = timeline.ReleaseLocalNs(senderWallNs: 5_400_000_000, localNowNs: 6_400_000_000);
        Assert.AreEqual(5_400_000_000 + 940_000_000 + 200_000_000, rLower, delta: 1_000_000);
    }

    [TestMethod]
    public async Task ReleasesFramesInReleaseTimeOrder()
    {
        long now = 1_000_000_000;
        Func<long> clock = () => Interlocked.Read(ref now);
        var order = new ConcurrentQueue<int>();
        using var done = new CountdownEvent(3);

        await using var scheduler = new PlayoutScheduler(fixedDelayNs: 0, clock);
        // Frames arrive in real time (local clock advances with capture), so the offset is steady and each
        // frame's release tracks its capture: frame i releases 1 ms after frame i-1.
        for (int i = 0; i < 3; i++)
        {
            int captured = i;
            Interlocked.Exchange(ref now, 1_000_000_000 + (i * 1_000_000L)); // arrival advances with capture
            scheduler.Schedule(i * 1_000_000L, () =>
            {
                order.Enqueue(captured);
                done.Signal();
            });
        }

        Interlocked.Exchange(ref now, 1_000_000_000 + 100_000_000); // 100 ms later: all due.
        Assert.IsTrue(done.Wait(TimeSpan.FromSeconds(5)), "all scheduled frames should release.");
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, order.ToArray());
    }

    [TestMethod]
    public async Task HoldsAFrameUntilItsReleaseTime()
    {
        long now = 1_000_000_000;
        Func<long> clock = () => Interlocked.Read(ref now);
        using var released = new ManualResetEventSlim(false);

        await using var scheduler = new PlayoutScheduler(fixedDelayNs: 500_000_000, clock); // 500 ms delay.
        scheduler.Schedule(senderWallNs: 0, () => released.Set());

        // The fake clock has not advanced, so within a real window the frame must NOT present early.
        Assert.IsFalse(released.Wait(TimeSpan.FromMilliseconds(300)), "frame presented before its scheduled time.");

        Interlocked.Exchange(ref now, 1_000_000_000 + 500_000_000); // advance to the release time.
        Assert.IsTrue(released.Wait(TimeSpan.FromSeconds(5)), "frame should present once due.");
    }

    [TestMethod]
    public async Task DisposeDrainsRemainingFrames()
    {
        long now = 1_000_000_000;
        Func<long> clock = () => Interlocked.Read(ref now);
        using var released = new ManualResetEventSlim(false);

        var scheduler = new PlayoutScheduler(fixedDelayNs: 10_000_000_000, clock); // 10 s: never due in-test.
        scheduler.Schedule(senderWallNs: 0, () => released.Set());
        Assert.IsFalse(released.Wait(TimeSpan.FromMilliseconds(100)));

        // Disposing must flush the still-queued frame rather than drop it.
        await scheduler.DisposeAsync();
        Assert.IsTrue(released.Wait(TimeSpan.FromSeconds(1)), "dispose should drain queued frames.");
    }
}
