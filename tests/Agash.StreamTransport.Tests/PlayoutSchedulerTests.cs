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
        MediaTime first = timeline.Release(PlayoutStream.Audio, Sender(5_000), Local(6_000));
        Assert.IsTrue(timeline.IsAnchored);
        Assert.AreEqual(Local(6_000) + Fixed, first);

        // The other stream's frame of the same instant arrives 30 ms later and releases with it.
        MediaTime second = timeline.Release(PlayoutStream.Video, Sender(5_000), Local(6_030));
        Assert.AreEqual(first.Nanoseconds, second.Nanoseconds, 1_000_000);
    }

    // Video runs 100 ms behind audio on every frame: that is its path, not jitter. Audio is held back by it,
    // so frames captured together release together, and the buffer is the lag plus the margin.
    [TestMethod]
    public void Timeline_SystematicVideoLag_HoldsAudioBackWithoutCountingAsJitter()
    {
        PlayoutTimeline timeline = Adaptive();
        (MediaTime Audio, MediaTime Video) last = default;
        for (int i = 0; i < 90; i++)
        {
            long captured = 5_000 + (i * 33);
            MediaTime audio = timeline.Release(
                PlayoutStream.Audio,
                Sender(captured),
                Local(captured + 1_000)
            );
            MediaTime video = timeline.Release(
                PlayoutStream.Video,
                Sender(captured),
                Local(captured + 1_100)
            );
            last = (audio, video);
        }

        Assert.AreEqual(120, timeline.CurrentDelay.TotalMilliseconds, 1, "the lag and the margin");
        Assert.AreEqual(last.Audio.Nanoseconds, last.Video.Nanoseconds, 1_000_000);
    }

    // Encoders opening and keyframes bursting make the first frames late; they do not size the buffer.
    [TestMethod]
    public void Timeline_StartupSpike_DoesNotSizeTheBuffer()
    {
        PlayoutTimeline timeline = Adaptive();
        for (int i = 0; i < 90; i++)
        {
            long captured = 5_000 + (i * 33);
            long late = i < 10 ? 300 : 0;
            _ = timeline.Release(
                PlayoutStream.Video,
                Sender(captured),
                Local(captured + 1_000 + late)
            );
        }

        Assert.AreEqual(20, timeline.TargetDelay.TotalMilliseconds, 1, "the margin alone");
    }

    // Half the frames 50 ms late: the 95th percentile covers them; a lone huge spike is clamped, not trusted.
    [TestMethod]
    public void Timeline_Jitter_IsCoveredAtItsPercentile_AndAnOutlierIsClamped()
    {
        PlayoutTimeline timeline = Adaptive();
        for (int i = 0; i < 120; i++)
        {
            long captured = 5_000 + (i * 33);
            long late = i % 2 == 0 ? 50 : 0;
            _ = timeline.Release(
                PlayoutStream.Video,
                Sender(captured),
                Local(captured + 1_000 + late)
            );
        }

        Assert.AreEqual(70, timeline.TargetDelay.TotalMilliseconds, 1, "the jitter and the margin");

        // A frame captured five seconds ago arrives now, among the others.
        long next = 5_000 + (120 * 33);
        _ = timeline.Release(PlayoutStream.Video, Sender(next - 5_000), Local(next + 1_000));
        Assert.AreEqual(
            70,
            timeline.TargetDelay.TotalMilliseconds,
            1,
            "one spike does not move the percentile"
        );
    }

    // The buffer shrinks at most 100 ms a second, so playout speeds up instead of skipping.
    [TestMethod]
    public void Timeline_Delay_ShrinksAtMost100MillisecondsASecond()
    {
        PlayoutTimeline timeline = Adaptive();
        long captured = 5_000;
        for (int i = 0; i < 90; i++, captured += 33)
        {
            long late = i >= 30 && i % 2 == 0 ? 300 : 0;
            _ = timeline.Release(
                PlayoutStream.Video,
                Sender(captured),
                Local(captured + 1_000 + late)
            );
        }

        double high = timeline.CurrentDelay.TotalMilliseconds;
        Assert.IsGreaterThan(250, high);

        // The jitter ends; two seconds on, the window has forgotten it, and the delay has come down by at most
        // a hundred milliseconds for each second.
        for (int i = 0; i < 90; i++, captured += 33)
        {
            _ = timeline.Release(PlayoutStream.Video, Sender(captured), Local(captured + 1_000));
        }

        double seconds = 90 * 0.033;
        Assert.IsGreaterThanOrEqualTo(
            high - (100 * seconds) - 1,
            timeline.CurrentDelay.TotalMilliseconds
        );
        Assert.IsLessThan(high, timeline.CurrentDelay.TotalMilliseconds);
    }

    private static PlayoutTimeline Adaptive() =>
        new(TimeSpan.Zero, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(20));

    [TestMethod]
    public async Task Scheduler_ReleasesInCaptureOrderWhenDue()
    {
        // Audio, which plays every due entry, so the order shows whatever arrives together.
        FakeTimeProvider time = new();
        Played played = new(time);
        await using PlayoutScheduler scheduler = new(
            new PlayoutTimeline(Fixed, Fixed, TimeSpan.Zero),
            new MediaClock(time)
        );
        var start = NtpTime.From(time.GetUtcNow());

        scheduler.Schedule(
            PlayoutStream.Audio,
            start + TimeSpan.FromMilliseconds(20),
            played.Entry(2)
        );
        scheduler.Schedule(PlayoutStream.Audio, start, played.Entry(0));
        scheduler.Schedule(
            PlayoutStream.Audio,
            start + TimeSpan.FromMilliseconds(10),
            played.Entry(1)
        );

        time.Advance(Fixed + TimeSpan.FromMilliseconds(30));
        await played.WaitForAsync(3);
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, played.Ids);
    }

    [TestMethod]
    public async Task Scheduler_VideoFallenBehind_ShowsOnlyTheLatestDuePicture()
    {
        FakeTimeProvider time = new();
        Played played = new(time);
        await using PlayoutScheduler scheduler = new(
            new PlayoutTimeline(Fixed, Fixed, TimeSpan.Zero),
            new MediaClock(time)
        );
        var start = NtpTime.From(time.GetUtcNow());
        ConcurrentQueue<int> late = [];
        Entry first = new(0, late);
        Entry second = new(1, late);
        scheduler.Schedule(PlayoutStream.Video, start, first);
        scheduler.Schedule(PlayoutStream.Video, start + TimeSpan.FromMilliseconds(10), second);
        scheduler.Schedule(
            PlayoutStream.Video,
            start + TimeSpan.FromMilliseconds(20),
            played.Entry(2)
        );

        // All three come due at once: the first two are already late for the third.
        time.Advance(Fixed + TimeSpan.FromMilliseconds(30));
        await played.WaitForAsync(1);

        CollectionAssert.AreEqual(new[] { 2 }, played.Ids);
        Assert.IsEmpty(late, "the late pictures were not shown");
        Assert.IsTrue(first.Disposed && second.Disposed, "the late pictures were released");
        Assert.AreEqual(2, scheduler.VideoFramesDropped);
    }

    [TestMethod]
    public async Task Scheduler_PictureOlderThanTheLastShown_IsNotShown()
    {
        FakeTimeProvider time = new();
        Played played = new(time);
        await using PlayoutScheduler scheduler = new(
            new PlayoutTimeline(Fixed, Fixed, TimeSpan.Zero),
            new MediaClock(time)
        );
        var start = NtpTime.From(time.GetUtcNow());
        scheduler.Schedule(
            PlayoutStream.Video,
            start + TimeSpan.FromMilliseconds(40),
            played.Entry(1)
        );
        time.Advance(Fixed);
        await played.WaitForAsync(1);

        // Captured before the one shown, it arrives after: showing it would step back in time.
        ConcurrentQueue<int> late = [];
        Entry earlier = new(0, late);
        scheduler.Schedule(PlayoutStream.Video, start, earlier);
        scheduler.Schedule(
            PlayoutStream.Video,
            start + TimeSpan.FromMilliseconds(80),
            played.Entry(2)
        );
        time.Advance(TimeSpan.FromMilliseconds(80));
        await played.WaitForAsync(2);

        CollectionAssert.AreEqual(new[] { 1, 2 }, played.Ids);
        Assert.IsEmpty(late);
        Assert.IsTrue(earlier.Disposed);
    }

    [TestMethod]
    public async Task Scheduler_AudioFallenBehind_PlaysEverySlot()
    {
        FakeTimeProvider time = new();
        Played played = new(time);
        await using PlayoutScheduler scheduler = new(
            new PlayoutTimeline(Fixed, Fixed, TimeSpan.Zero),
            new MediaClock(time)
        );
        var start = NtpTime.From(time.GetUtcNow());
        for (int i = 0; i < 3; i++)
        {
            scheduler.Schedule(
                PlayoutStream.Audio,
                start + TimeSpan.FromMilliseconds(10 * i),
                played.Entry(i)
            );
        }

        time.Advance(Fixed + TimeSpan.FromMilliseconds(30));
        await played.WaitForAsync(3);

        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, played.Ids, "a gap in sound is heard");
    }

    [TestMethod]
    public async Task Scheduler_SlowVideoSink_DoesNotHoldBackAudio()
    {
        FakeTimeProvider time = new();
        Played played = new(time);
        await using PlayoutScheduler scheduler = new(
            new PlayoutTimeline(Fixed, Fixed, TimeSpan.Zero),
            new MediaClock(time)
        );
        var start = NtpTime.From(time.GetUtcNow());
        using Blocking video = new();
        scheduler.Schedule(PlayoutStream.Video, start, video);
        scheduler.Schedule(PlayoutStream.Audio, start, played.Entry(0));

        time.Advance(Fixed);
        await video.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await played.WaitForAsync(1);

        CollectionAssert.AreEqual(new[] { 0 }, played.Ids, "audio played while video was stuck");
        video.Release();
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

        scheduler.Schedule(PlayoutStream.Video, NtpTime.From(time.GetUtcNow()), played.Entry(0));
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

        scheduler.Schedule(
            PlayoutStream.Audio,
            now,
            played.Entry(1),
            TimeSpan.FromMilliseconds(50)
        );
        scheduler.Schedule(PlayoutStream.Video, now, played.Entry(0));
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
        scheduler.Schedule(PlayoutStream.Video, NtpTime.From(time.GetUtcNow()), entry);

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

    // A video sink that does not return until released.
    private sealed class Blocking : IPlayoutEntry
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly ManualResetEventSlim _release = new();

        public Task Entered => _entered.Task;

        public void Play()
        {
            _entered.TrySetResult();
            _release.Wait(TimeSpan.FromSeconds(10));
        }

        public void Release() => _release.Set();

        public void Dispose() => _release.Set();
    }

    private sealed class Entry(int id, ConcurrentQueue<int> played) : IPlayoutEntry
    {
        public bool Disposed { get; private set; }

        public void Play() => played.Enqueue(id);

        public void Dispose() => Disposed = true;
    }
}
