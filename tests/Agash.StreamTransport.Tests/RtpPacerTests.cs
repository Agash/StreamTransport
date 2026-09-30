using System.Buffers;
using System.Collections.Concurrent;
using Agash.StreamTransport.Rtp;
using Microsoft.Extensions.Time.Testing;

namespace Agash.StreamTransport.Tests;

[TestClass]
public sealed class RtpPacerTests
{
    private const uint VideoSsrc = 1;
    private const uint AudioSsrc = 2;

    [TestMethod]
    public async Task Video_IsSpreadOverTimeAtThePacingRate()
    {
        FakeTimeProvider time = new();
        ConcurrentQueue<(uint Ssrc, TimeSpan At)> sent = [];
        DateTimeOffset start = time.GetUtcNow();
        await using RtpPacer pacer = new(Recorder(sent, time, start), 800_000, time);

        // 20 packets of 1000 bytes at 100 kB/s take about 200 ms.
        for (int i = 0; i < 20; i++)
        {
            pacer.EnqueueVideo(Packet(VideoSsrc, 1000));
        }

        await RunAsync(time, TimeSpan.FromMilliseconds(250), () => sent.Count == 20);
        TimeSpan last = sent.Last().At;
        Assert.IsTrue(
            last >= TimeSpan.FromMilliseconds(180) && last <= TimeSpan.FromMilliseconds(220),
            $"The last packet went at {last.TotalMilliseconds} ms."
        );
    }

    [TestMethod]
    public async Task Audio_GoesAheadOfWaitingVideo()
    {
        FakeTimeProvider time = new();
        ConcurrentQueue<(uint Ssrc, TimeSpan At)> sent = [];
        DateTimeOffset start = time.GetUtcNow();
        await using RtpPacer pacer = new(Recorder(sent, time, start), 800_000, time);
        for (int i = 0; i < 20; i++)
        {
            pacer.EnqueueVideo(Packet(VideoSsrc, 1000));
        }

        await RunAsync(time, TimeSpan.FromMilliseconds(50), () => false);
        pacer.EnqueueAudio(Packet(AudioSsrc, 100));
        await WaitForAsync(() => sent.Any(static p => p.Ssrc == AudioSsrc));

        (uint _, TimeSpan audioAt) = sent.First(static p => p.Ssrc == AudioSsrc);
        Assert.AreEqual(
            50,
            audioAt.TotalMilliseconds,
            10,
            "audio left without waiting for the video budget"
        );
        Assert.IsLessThan(
            20,
            sent.Count(static p => p.Ssrc == VideoSsrc),
            "video was still queued"
        );
    }

    [TestMethod]
    public async Task StandingQueue_DrainsWithinTheQueueDelayLimit()
    {
        FakeTimeProvider time = new();
        ConcurrentQueue<(uint Ssrc, TimeSpan At)> sent = [];
        DateTimeOffset start = time.GetUtcNow();

        // 100 kB at 100 kB/s would take a second; the limit caps the wait at 250 ms.
        await using RtpPacer pacer = new(Recorder(sent, time, start), 800_000, time);
        for (int i = 0; i < 100; i++)
        {
            pacer.EnqueueVideo(Packet(VideoSsrc, 1000));
        }

        await RunAsync(time, TimeSpan.FromMilliseconds(400), () => sent.Count == 100);
        Assert.IsLessThanOrEqualTo(
            RtpPacer.MaxQueueDelay + TimeSpan.FromMilliseconds(20),
            sent.Last().At
        );
    }

    [TestMethod]
    public async Task ZeroRate_SendsAtOnce()
    {
        FakeTimeProvider time = new();
        ConcurrentQueue<(uint Ssrc, TimeSpan At)> sent = [];
        await using RtpPacer pacer = new(Recorder(sent, time, time.GetUtcNow()), 0, time);
        for (int i = 0; i < 10; i++)
        {
            pacer.EnqueueVideo(Packet(VideoSsrc, 1000));
        }

        await WaitForAsync(() => sent.Count == 10);
        Assert.IsTrue(sent.All(static p => p.At == TimeSpan.Zero));
    }

    [TestMethod]
    public async Task FailedSend_IsDroppedAndTheNextOneGoes()
    {
        FakeTimeProvider time = new();
        int attempts = 0;
        ConcurrentQueue<uint> sent = [];
        await using RtpPacer pacer = new(
            (packet, _) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new InvalidOperationException("The socket closed.");
                }

                sent.Enqueue(packet.Ssrc);
                return ValueTask.CompletedTask;
            },
            0,
            time
        );

        pacer.EnqueueVideo(Packet(VideoSsrc, 100));
        pacer.EnqueueVideo(Packet(VideoSsrc, 100));

        await WaitForAsync(() => sent.Count == 1);
        Assert.AreEqual(2, attempts);
    }

    private static PacedPacket Packet(uint ssrc, int length) =>
        new(96, ssrc, 0, false, ArrayPool<byte>.Shared.Rent(length), length, 0);

    private static Func<PacedPacket, CancellationToken, ValueTask> Recorder(
        ConcurrentQueue<(uint, TimeSpan)> sent,
        FakeTimeProvider time,
        DateTimeOffset start
    ) =>
        (packet, _) =>
        {
            sent.Enqueue((packet.Ssrc, time.GetUtcNow() - start));
            return ValueTask.CompletedTask;
        };

    // Advances simulated time in 1 ms steps, letting the pacer's loop run between them.
    private static async Task RunAsync(FakeTimeProvider time, TimeSpan duration, Func<bool> done)
    {
        for (
            TimeSpan elapsed = TimeSpan.Zero;
            elapsed < duration && !done();
            elapsed += TimeSpan.FromMilliseconds(1)
        )
        {
            await SettleAsync();
            time.Advance(TimeSpan.FromMilliseconds(1));
        }

        await SettleAsync();
    }

    private static async Task SettleAsync()
    {
        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(1);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(condition());
    }
}
