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
        Played played = new(time);
        await using PlayoutScheduler scheduler = new(
            new PlayoutTimeline(Fixed, Fixed, TimeSpan.Zero),
            new MediaClock(time)
        );
        var start = NtpTime.From(time.GetUtcNow());

        scheduler.Schedule(start + TimeSpan.FromMilliseconds(20), played.Entry(2));
        scheduler.Schedule(start, played.Entry(0));
        scheduler.Schedule(start + TimeSpan.FromMilliseconds(10), played.Entry(1));

        time.Advance(Fixed + TimeSpan.FromMilliseconds(30));
        await played.WaitForAsync(3);
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, played.Ids);
    }

    [TestMethod]
    public async Task Scheduler_PlaysAnEntryWhenItsSlotComes()
    {
        FakeTimeProvider time = new();
        Played played = new(time);
        await using PlayoutScheduler scheduler = new(
            new PlayoutTimeline(Fixed, Fixed, TimeSpan.Zero),
            new MediaClock(time)
        );

        scheduler.Schedule(NtpTime.From(time.GetUtcNow()), played.Entry(0));
        time.Advance(Fixed - TimeSpan.FromMilliseconds(1));
        time.Advance(TimeSpan.FromMilliseconds(1));
        await played.WaitForAsync(1);

        Assert.AreEqual(Fixed, played.Times[0], "played at its slot, not before");
    }

    [TestMethod]
    public async Task Scheduler_ExtraDelay_ShiftsTheSlot()
    {
        FakeTimeProvider time = new();
        Played played = new(time);
        await using PlayoutScheduler scheduler = new(
            new PlayoutTimeline(Fixed, Fixed, TimeSpan.Zero),
            new MediaClock(time)
        );
        var now = NtpTime.From(time.GetUtcNow());

        scheduler.Schedule(now, played.Entry(1), TimeSpan.FromMilliseconds(50));
        scheduler.Schedule(now, played.Entry(0));
        time.Advance(Fixed);
        await played.WaitForAsync(1);
        time.Advance(TimeSpan.FromMilliseconds(50));
        await played.WaitForAsync(2);

        CollectionAssert.AreEqual(new[] { 0, 1 }, played.Ids);
        Assert.AreEqual(Fixed + TimeSpan.FromMilliseconds(50), played.Times[1]);
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

    // Records which entries played, and when on the fake clock, and signals as they do.
    private sealed class Played(FakeTimeProvider time)
    {
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
        private readonly DateTimeOffset _start = time.GetUtcNow();
        private readonly Lock _gate = new();
        private readonly List<(int Id, TimeSpan At)> _played = [];
        private readonly List<(int Count, TaskCompletionSource Reached)> _waiting = [];

        public int[] Ids
        {
            get
            {
                lock (_gate)
                {
                    return [.. _played.Select(static p => p.Id)];
                }
            }
        }

        public TimeSpan[] Times
        {
            get
            {
                lock (_gate)
                {
                    return [.. _played.Select(static p => p.At)];
                }
            }
        }

        public IPlayoutEntry Entry(int id) => new Recording(this, id);

        public Task WaitForAsync(int count)
        {
            TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_played.Count >= count)
                {
                    return Task.CompletedTask;
                }

                _waiting.Add((count, reached));
            }

            return reached.Task.WaitAsync(Guard);
        }

        private void Record(int id)
        {
            lock (_gate)
            {
                _played.Add((id, time.GetUtcNow() - _start));
                foreach ((int count, TaskCompletionSource reached) in _waiting)
                {
                    if (_played.Count >= count)
                    {
                        reached.TrySetResult();
                    }
                }
            }
        }

        private sealed class Recording(Played played, int id) : IPlayoutEntry
        {
            public void Play() => played.Record(id);

            public void Dispose() { }
        }
    }

    private sealed class Entry(int id, ConcurrentQueue<int> played) : IPlayoutEntry
    {
        public bool Disposed { get; private set; }

        public void Play() => played.Enqueue(id);

        public void Dispose() => Disposed = true;
    }
}
