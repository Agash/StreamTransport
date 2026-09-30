using System.Buffers;
using Agash.StreamTransport.Rtp;
using Microsoft.Extensions.Time.Testing;

namespace Agash.StreamTransport.Tests;

[TestClass]
public sealed class RtpPacerTests
{
    private const uint VideoSsrc = 1;
    private const uint AudioSsrc = 2;

    // Guards against a hang; every wait here ends on a signal long before it.
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void Queue_SpreadsVideoOverTimeAtTheRate()
    {
        // 20 packets of 1000 bytes at 100 kB/s take 200 ms.
        PacingQueue queue = new() { BitsPerSecond = 800_000 };
        for (int i = 0; i < 20; i++)
        {
            queue.EnqueueVideo(Packet(VideoSsrc, 1000), TimeSpan.Zero);
        }

        List<(uint Ssrc, TimeSpan At)> sent = Run(queue, TimeSpan.Zero);

        Assert.HasCount(20, sent);
        Assert.AreEqual(
            10,
            sent[0].At.TotalMilliseconds,
            0.01,
            "the first waits for its own budget"
        );
        Assert.AreEqual(200, sent[^1].At.TotalMilliseconds, 0.01);
    }

    [TestMethod]
    public void Queue_AudioGoesAheadOfWaitingVideo()
    {
        PacingQueue queue = new() { BitsPerSecond = 800_000 };
        for (int i = 0; i < 5; i++)
        {
            queue.EnqueueVideo(Packet(VideoSsrc, 1000), TimeSpan.Zero);
        }

        Assert.IsNull(queue.Next(TimeSpan.Zero).Packet, "video waits for budget");
        queue.EnqueueAudio(Packet(AudioSsrc, 100));
        Assert.AreEqual(AudioSsrc, queue.Next(TimeSpan.Zero).Packet?.Ssrc, "audio does not");
    }

    [TestMethod]
    public void Queue_StandingQueueDrainsWithinTheDelayLimit()
    {
        // 100 kB at 100 kB/s would take a second; the limit caps the wait.
        PacingQueue queue = new() { BitsPerSecond = 800_000 };
        for (int i = 0; i < 100; i++)
        {
            queue.EnqueueVideo(Packet(VideoSsrc, 1000), TimeSpan.Zero);
        }

        List<(uint Ssrc, TimeSpan At)> sent = Run(queue, TimeSpan.Zero);

        Assert.HasCount(100, sent);
        Assert.IsLessThanOrEqualTo(PacingQueue.MaxQueueDelay, sent[^1].At);
    }

    [TestMethod]
    public void Queue_IdleTimeSavesOnlyAShortBurst()
    {
        PacingQueue queue = new() { BitsPerSecond = 800_000 };
        queue.EnqueueVideo(Packet(VideoSsrc, 1000), TimeSpan.Zero);
        _ = Run(queue, TimeSpan.Zero);

        // After a second idle, ten packets do not all leave at once.
        var later = TimeSpan.FromSeconds(1);
        for (int i = 0; i < 10; i++)
        {
            queue.EnqueueVideo(Packet(VideoSsrc, 1000), later);
        }

        List<(uint Ssrc, TimeSpan At)> sent = Run(queue, later);
        Assert.AreEqual(later, sent[0].At, "one packet's budget was saved");
        Assert.IsGreaterThan(later, sent[1].At, "the rest are paced");
    }

    [TestMethod]
    public void Queue_ZeroRate_SendsAtOnce()
    {
        PacingQueue queue = new();
        for (int i = 0; i < 10; i++)
        {
            queue.EnqueueVideo(Packet(VideoSsrc, 1000), TimeSpan.Zero);
        }

        Assert.IsTrue(Run(queue, TimeSpan.Zero).All(static p => p.At == TimeSpan.Zero));
    }

    [TestMethod]
    public async Task Pacer_SendsWhenTheClockReachesEachPacketsTurn()
    {
        FakeTimeProvider time = new();
        Sends sends = new(time);
        await using RtpPacer pacer = new(sends.Send, 800_000, time);
        pacer.EnqueueVideo(Packet(VideoSsrc, 1000));
        pacer.EnqueueVideo(Packet(VideoSsrc, 1000));

        time.Advance(TimeSpan.FromMilliseconds(10));
        await sends.WaitForAsync(1);
        time.Advance(TimeSpan.FromMilliseconds(10));
        await sends.WaitForAsync(2);

        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20) },
            sends.Times
        );
    }

    [TestMethod]
    public async Task Pacer_FailedSendIsDroppedAndTheNextOneGoes()
    {
        FakeTimeProvider time = new();
        Sends sends = new(time, failFirst: true);
        await using RtpPacer pacer = new(sends.Send, 0, time);

        pacer.EnqueueVideo(Packet(VideoSsrc, 100));
        pacer.EnqueueVideo(Packet(VideoSsrc, 100));

        await sends.WaitForAsync(1);
        Assert.AreEqual(2, sends.Attempts);
    }

    // Steps the queue as the pacer's loop does, jumping to each wait's end.
    private static List<(uint Ssrc, TimeSpan At)> Run(PacingQueue queue, TimeSpan now)
    {
        List<(uint, TimeSpan)> sent = [];
        while (true)
        {
            PacingStep step = queue.Next(now);
            if (step.Packet is { } packet)
            {
                sent.Add((packet.Ssrc, now));
                ArrayPool<byte>.Shared.Return(packet.Buffer);
            }
            else if (step.Wait is { } wait)
            {
                now += wait;
            }
            else
            {
                return sent;
            }
        }
    }

    private static PacedPacket Packet(uint ssrc, int length) =>
        new(96, ssrc, 0, false, ArrayPool<byte>.Shared.Rent(length), length, 0);

    // Records when each packet went, on the fake clock, and signals as the count grows.
    private sealed class Sends(FakeTimeProvider time, bool failFirst = false)
    {
        private readonly DateTimeOffset _start = time.GetUtcNow();
        private readonly Lock _gate = new();
        private readonly List<TimeSpan> _times = [];
        private readonly List<(int Count, TaskCompletionSource Reached)> _waiting = [];
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public TimeSpan[] Times
        {
            get
            {
                lock (_gate)
                {
                    return [.. _times];
                }
            }
        }

        public ValueTask Send(PacedPacket packet, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) == 1 && failFirst)
            {
                throw new InvalidOperationException("The socket closed.");
            }

            lock (_gate)
            {
                _times.Add(time.GetUtcNow() - _start);
                foreach ((int count, TaskCompletionSource reached) in _waiting)
                {
                    if (_times.Count >= count)
                    {
                        reached.TrySetResult();
                    }
                }
            }

            return ValueTask.CompletedTask;
        }

        public Task WaitForAsync(int count)
        {
            TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_times.Count >= count)
                {
                    return Task.CompletedTask;
                }

                _waiting.Add((count, reached));
            }

            return reached.Task.WaitAsync(Guard);
        }
    }
}
