using System.Buffers;
using Microsoft.Extensions.Time.Testing;

namespace Agash.StreamTransport.Adaptation.Tests;

[TestClass]
public sealed class PacerTests
{
    // Guards against a hang; every wait here ends on a signal long before it.
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void Queue_SpreadsVideoOverTimeAtTheRate()
    {
        // 20 packets of 1000 bytes at 100 kB/s take 200 ms.
        PacingQueue queue = new() { BitsPerSecond = 800_000 };
        for (int i = 0; i < 20; i++)
        {
            queue.Enqueue(Packet(TrafficClass.Video, 1000), TimeSpan.Zero);
        }

        List<(TrafficClass Class, TimeSpan At)> sent = Run(queue, TimeSpan.Zero);

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
            queue.Enqueue(Packet(TrafficClass.Video, 1000), TimeSpan.Zero);
        }

        Assert.IsNull(queue.Next(TimeSpan.Zero).Packet, "video waits for budget");
        queue.Enqueue(Packet(TrafficClass.Audio, 100), TimeSpan.Zero);
        Assert.AreEqual(
            TrafficClass.Audio,
            queue.Next(TimeSpan.Zero).Packet?.Class,
            "audio does not"
        );
    }

    // Retransmissions go before queued video, and FEC repair after it.
    [TestMethod]
    public void Queue_ClassesGoInPriorityOrder()
    {
        PacingQueue queue = new() { BitsPerSecond = 800_000 };
        queue.Enqueue(Packet(TrafficClass.Repair, 500), TimeSpan.Zero);
        queue.Enqueue(Packet(TrafficClass.Video, 1000), TimeSpan.Zero);
        queue.Enqueue(Packet(TrafficClass.Retransmission, 1000), TimeSpan.Zero);
        queue.Enqueue(Packet(TrafficClass.Audio, 100), TimeSpan.Zero);

        TrafficClass[] order = [.. Run(queue, TimeSpan.Zero).Select(static p => p.Class)];

        CollectionAssert.AreEqual(
            new[]
            {
                TrafficClass.Audio,
                TrafficClass.Retransmission,
                TrafficClass.Video,
                TrafficClass.Repair,
            },
            order
        );
    }

    // Recovery traffic spends the same budget as video: adding it delays the video by its size.
    [TestMethod]
    public void Queue_RecoveryTrafficSharesTheBudget()
    {
        PacingQueue queue = new() { BitsPerSecond = 800_000 };
        for (int i = 0; i < 10; i++)
        {
            queue.Enqueue(Packet(TrafficClass.Video, 1000), TimeSpan.Zero);
        }

        for (int i = 0; i < 5; i++)
        {
            queue.Enqueue(Packet(TrafficClass.Retransmission, 1000), TimeSpan.Zero);
            queue.Enqueue(Packet(TrafficClass.Repair, 1000), TimeSpan.Zero);
        }

        List<(TrafficClass Class, TimeSpan At)> sent = Run(queue, TimeSpan.Zero);

        // 20 kB at 100 kB/s: the last packet leaves at 200 ms, as 20 video packets alone would.
        Assert.HasCount(20, sent);
        Assert.AreEqual(200, sent[^1].At.TotalMilliseconds, 0.01);
    }

    [TestMethod]
    public void Queue_PacketOverheadCountsAgainstTheBudget()
    {
        // 900 bytes plus 100 of overhead each: 20 packets at 100 kB/s take 200 ms.
        PacingQueue queue = new() { BitsPerSecond = 800_000, PacketOverhead = 100 };
        for (int i = 0; i < 20; i++)
        {
            queue.Enqueue(Packet(TrafficClass.Video, 900), TimeSpan.Zero);
        }

        Assert.AreEqual(200, Run(queue, TimeSpan.Zero)[^1].At.TotalMilliseconds, 0.01);
    }

    [TestMethod]
    public void Queue_StandingQueueDrainsWithinTheDelayLimit()
    {
        // 100 kB at 100 kB/s would take a second; the limit caps the wait.
        PacingQueue queue = new() { BitsPerSecond = 800_000 };
        for (int i = 0; i < 100; i++)
        {
            queue.Enqueue(Packet(TrafficClass.Video, 1000), TimeSpan.Zero);
        }

        List<(TrafficClass Class, TimeSpan At)> sent = Run(queue, TimeSpan.Zero);

        Assert.HasCount(100, sent);
        Assert.IsLessThanOrEqualTo(PacingQueue.MaxQueueDelay, sent[^1].At);
    }

    [TestMethod]
    public void Queue_IdleTimeSavesOnlyAShortBurst()
    {
        PacingQueue queue = new() { BitsPerSecond = 800_000 };
        queue.Enqueue(Packet(TrafficClass.Video, 1000), TimeSpan.Zero);
        _ = Run(queue, TimeSpan.Zero);

        // After a second idle, ten packets do not all leave at once.
        var later = TimeSpan.FromSeconds(1);
        for (int i = 0; i < 10; i++)
        {
            queue.Enqueue(Packet(TrafficClass.Video, 1000), later);
        }

        List<(TrafficClass Class, TimeSpan At)> sent = Run(queue, later);
        Assert.AreEqual(later, sent[0].At, "one packet's budget was saved");
        Assert.IsGreaterThan(later, sent[1].At, "the rest are paced");
    }

    [TestMethod]
    public void Queue_ZeroRate_SendsAtOnce()
    {
        PacingQueue queue = new();
        for (int i = 0; i < 10; i++)
        {
            queue.Enqueue(Packet(TrafficClass.Video, 1000), TimeSpan.Zero);
        }

        Assert.IsTrue(Run(queue, TimeSpan.Zero).All(static p => p.At == TimeSpan.Zero));
    }

    [TestMethod]
    public async Task Pacer_SendsWhenTheClockReachesEachPacketsTurn()
    {
        FakeTimeProvider time = new();
        Sends sends = new(time);
        await using Pacer pacer = new(sends.Send, timeProvider: time) { BitsPerSecond = 800_000 };
        pacer.Enqueue(Packet(TrafficClass.Video, 1000));
        pacer.Enqueue(Packet(TrafficClass.Video, 1000));

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
        await using Pacer pacer = new(sends.Send, timeProvider: time);

        pacer.Enqueue(Packet(TrafficClass.Video, 100));
        pacer.Enqueue(Packet(TrafficClass.Video, 100));

        await sends.WaitForAsync(1);
        Assert.AreEqual(2, sends.Attempts);
    }

    [TestMethod]
    public async Task Pacer_CountsSentBytesPerClassWithOverhead()
    {
        FakeTimeProvider time = new();
        Sends sends = new(time);
        await using Pacer pacer = new(sends.Send, packetOverhead: 40, timeProvider: time);

        pacer.Enqueue(Packet(TrafficClass.Retransmission, 1000));
        pacer.Enqueue(Packet(TrafficClass.Repair, 500));
        pacer.Enqueue(Packet(TrafficClass.Repair, 500));
        await sends.WaitForAsync(3);

        Assert.AreEqual(1040, pacer.SentBytes(TrafficClass.Retransmission));
        Assert.AreEqual(1080, pacer.SentBytes(TrafficClass.Repair));
        Assert.AreEqual(0, pacer.SentBytes(TrafficClass.Video));
    }

    // Steps the queue as the pacer's loop does, jumping to each wait's end.
    private static List<(TrafficClass Class, TimeSpan At)> Run(PacingQueue queue, TimeSpan now)
    {
        List<(TrafficClass, TimeSpan)> sent = [];
        while (true)
        {
            PacingStep step = queue.Next(now);
            if (step.Packet is { } packet)
            {
                sent.Add((packet.Class, now));
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

    private static PacedPacket Packet(TrafficClass trafficClass, int length) =>
        new(ArrayPool<byte>.Shared.Rent(length), length, trafficClass);

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
