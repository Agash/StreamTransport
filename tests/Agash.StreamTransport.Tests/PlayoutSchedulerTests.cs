using System.Collections.Concurrent;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.Sync;
using Microsoft.Extensions.Time.Testing;

namespace Agash.StreamTransport.Tests;

[TestClass]
public sealed class PlayoutSchedulerTests
{
    private static readonly TimeSpan Fixed = TimeSpan.FromMilliseconds(200);

    [TestMethod]
    public void Timeline_CoCapturedFramesOfBothStreams_ReleaseTogether()
    {
        PlayoutTimeline timeline = new(Fixed, Fixed, TimeSpan.Zero);
        Assert.IsFalse(timeline.IsAnchored);

        // Captured at sender 5 s, arriving at local 6 s: the offset is 1 s.
        MediaTime first = timeline.Release(Sender(5_000), Local(6_000));
        Assert.IsTrue(timeline.IsAnchored);
        Assert.AreEqual(Local(6_000) + Fixed, first);

        // The other stream's frame of the same instant arrives 30 ms later and releases with it.
        MediaTime second = timeline.Release(Sender(5_000), Local(6_030));
        Assert.AreEqual(first.Nanoseconds, second.Nanoseconds, 1_000_000);
    }

    [TestMethod]
    public void Timeline_LateTransient_DoesNotRaiseTheOffsetButAFasterPathLowersIt()
    {
        PlayoutTimeline timeline = new(Fixed, Fixed, TimeSpan.Zero);
        _ = timeline.Release(Sender(5_000), Local(6_000));

        // 200 ms late: the minimum offset keeps the next normal frame on 1 s.
        _ = timeline.Release(Sender(5_100), Local(6_300));
        MediaTime normal = timeline.Release(Sender(5_200), Local(6_200));
        Assert.AreEqual((Local(6_200) + Fixed).Nanoseconds, normal.Nanoseconds, 1_000_000);

        // A path 60 ms faster is taken at once.
        _ = timeline.Release(Sender(5_300), Local(6_240));
        MediaTime lower = timeline.Release(Sender(5_400), Local(6_340));
        Assert.AreEqual((Local(6_340) + Fixed).Nanoseconds, lower.Nanoseconds, 1_000_000);
    }

    [TestMethod]
    public void Timeline_Jitter_GrowsTheBufferWithinItsBounds()
    {
        PlayoutTimeline timeline = new(
            TimeSpan.FromMilliseconds(40),
            TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(20)
        );
        _ = timeline.Release(Sender(5_000), Local(6_000));
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(40),
            timeline.CurrentDelay,
            "a clean link sits at the floor"
        );

        _ = timeline.Release(Sender(5_100), Local(6_200));
        Assert.AreEqual(
            120,
            timeline.CurrentDelay.TotalMilliseconds,
            0.1,
            "100 ms of jitter plus the margin"
        );

        _ = timeline.Release(Sender(5_200), Local(6_700));
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(300),
            timeline.CurrentDelay,
            "capped at the maximum"
        );
    }

    [TestMethod]
    public async Task Scheduler_ReleasesInCaptureOrderWhenDue()
    {
        FakeTimeProvider time = new();
        MediaClock clock = new(time);
        ConcurrentQueue<int> played = [];
        await using PlayoutScheduler scheduler = new(
            new PlayoutTimeline(Fixed, Fixed, TimeSpan.Zero),
            clock
        );
        var start = NtpTime.From(time.GetUtcNow());

        scheduler.Schedule(start + TimeSpan.FromMilliseconds(20), new Entry(2, played));
        scheduler.Schedule(start, new Entry(0, played));
        scheduler.Schedule(start + TimeSpan.FromMilliseconds(10), new Entry(1, played));

        await Settle();
        Assert.IsEmpty(played, "nothing plays before its slot");

        time.Advance(Fixed + TimeSpan.FromMilliseconds(30));
        await WaitForAsync(() => played.Count == 3);
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, played.ToArray());
    }

    [TestMethod]
    public async Task Scheduler_HoldsAnEntryUntilItsSlot()
    {
        FakeTimeProvider time = new();
        ConcurrentQueue<int> played = [];
        await using PlayoutScheduler scheduler = new(
            new PlayoutTimeline(Fixed, Fixed, TimeSpan.Zero),
            new MediaClock(time)
        );

        scheduler.Schedule(NtpTime.From(time.GetUtcNow()), new Entry(0, played));
        time.Advance(Fixed - TimeSpan.FromMilliseconds(1));
        await Settle();
        Assert.IsEmpty(played);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitForAsync(() => played.Count == 1);
    }

    [TestMethod]
    public async Task Scheduler_ExtraDelay_ShiftsTheSlot()
    {
        FakeTimeProvider time = new();
        ConcurrentQueue<int> played = [];
        await using PlayoutScheduler scheduler = new(
            new PlayoutTimeline(Fixed, Fixed, TimeSpan.Zero),
            new MediaClock(time)
        );
        var now = NtpTime.From(time.GetUtcNow());

        scheduler.Schedule(now, new Entry(1, played), TimeSpan.FromMilliseconds(50));
        scheduler.Schedule(now, new Entry(0, played));
        time.Advance(Fixed);
        await WaitForAsync(() => played.Count == 1);
        time.Advance(TimeSpan.FromMilliseconds(50));
        await WaitForAsync(() => played.Count == 2);
        CollectionAssert.AreEqual(new[] { 0, 1 }, played.ToArray());
    }

    [TestMethod]
    public async Task Dispose_ReleasesQueuedEntriesWithoutPlayingThem()
    {
        FakeTimeProvider time = new();
        ConcurrentQueue<int> played = [];
        Entry entry = new(0, played);
        PlayoutScheduler scheduler = new(
            new PlayoutTimeline(Fixed, Fixed, TimeSpan.Zero),
            new MediaClock(time)
        );
        scheduler.Schedule(NtpTime.From(time.GetUtcNow()), entry);

        await scheduler.DisposeAsync();

        Assert.IsEmpty(played);
        Assert.IsTrue(entry.Disposed);
    }

    [TestMethod]
    public void Aligner_MapsTimestampsAroundItsAnchorAndAcrossTheWrap()
    {
        RtpClockAligner aligner = new(new ClockRate(90_000));
        Assert.IsFalse(aligner.TryGetCapture(0, out _));

        var anchor = NtpTime.FromNanoseconds(3_900_000_000_000_000_000);
        aligner.Record(anchor, uint.MaxValue - 44_999);

        Assert.IsTrue(aligner.TryGetCapture(45_000, out NtpTime later));
        Assert.AreEqual(1_000_000_000, later.Nanoseconds - anchor.Nanoseconds, 1_000);
        Assert.IsTrue(aligner.TryGetCapture(uint.MaxValue - 89_999, out NtpTime earlier));
        Assert.AreEqual(-500_000_000, earlier.Nanoseconds - anchor.Nanoseconds, 1_000);
    }

    [TestMethod]
    public void NtpTime_RoundTripsWallClockTime()
    {
        DateTimeOffset instant = new(2026, 9, 30, 12, 0, 0, 250, TimeSpan.Zero);
        var ntp = NtpTime.From(instant);

        Assert.AreEqual(3_999_758_400UL, ntp.Value >> 32, "seconds since 1900");
        Assert.AreEqual(250_000_000, ntp.Nanoseconds % 1_000_000_000, 1);
    }

    private static NtpTime Sender(long milliseconds) =>
        NtpTime.FromNanoseconds(milliseconds * 1_000_000);

    private static MediaTime Local(long milliseconds) => new(milliseconds * 1_000_000);

    // Lets the scheduler's loop run after the fake clock moves.
    private static Task Settle() => Task.Delay(50);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(condition());
    }

    private sealed class Entry(int id, ConcurrentQueue<int> played) : IPlayoutEntry
    {
        public bool Disposed { get; private set; }

        public void Play() => played.Enqueue(id);

        public void Dispose() => Disposed = true;
    }
}
