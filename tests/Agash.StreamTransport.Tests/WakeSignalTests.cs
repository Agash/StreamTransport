using Agash.StreamTransport.Threading;
using Microsoft.Extensions.Time.Testing;

namespace Agash.StreamTransport.Tests;

[TestClass]
public sealed class WakeSignalTests
{
    [TestMethod]
    public async Task Signal_BeforeTheWait_IsTakenAtOnce()
    {
        WakeSignal wake = new();
        wake.Signal();
        wake.Signal();

        ValueTask<bool> first = wake.WaitAsync(CancellationToken.None);
        Assert.IsTrue(first.IsCompletedSuccessfully, "a pending signal completes the wait at once");
        Assert.IsTrue(await first);
        Assert.IsFalse(
            wake.WaitAsync(CancellationToken.None).IsCompleted,
            "two signals before a wait are one"
        );
    }

    [TestMethod]
    public async Task Signal_WakesAWaitingLoop()
    {
        WakeSignal wake = new();
        ValueTask<bool> waiting = wake.WaitAsync(CancellationToken.None);
        Assert.IsFalse(waiting.IsCompleted);

        wake.Signal();

        Assert.IsTrue(await waiting.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task WaitUntil_DeadlinePasses_ReturnsFalseWithoutThrowing()
    {
        FakeTimeProvider time = new();
        WakeSignal wake = new(time, () => time.GetUtcNow() - DateTimeOffset.UnixEpoch);
        TimeSpan now = time.GetUtcNow() - DateTimeOffset.UnixEpoch;

        ValueTask<bool> waiting = wake.WaitUntilAsync(
            now + TimeSpan.FromMilliseconds(50),
            CancellationToken.None
        );
        Assert.IsFalse(waiting.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(50));

        Assert.IsFalse(await waiting.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(
            await wake.WaitUntilAsync(now, CancellationToken.None),
            "a deadline already passed ends the wait at once"
        );
    }

    [TestMethod]
    public async Task WaitUntil_DeadlinePassingWhileTheTimerIsArmed_EndsTheWait()
    {
        // The loop's clock reaches the deadline between the remaining time being read and the timer
        // being armed for it, so the timer would only fire that much later than the deadline.
        FakeTimeProvider time = new();
        int reads = 0;
        var deadline = TimeSpan.FromSeconds(1);
        WakeSignal wake = new(time, () => ++reads == 1 ? TimeSpan.Zero : deadline);

        ValueTask<bool> waiting = wake.WaitUntilAsync(deadline, CancellationToken.None);

        Assert.IsTrue(waiting.IsCompleted, "the clock already says the deadline passed");
        Assert.IsFalse(await waiting);
    }

    [TestMethod]
    public async Task WaitUntil_TimerOfAnEarlierWait_DoesNotEndALaterOne()
    {
        FakeTimeProvider time = new();
        WakeSignal wake = new(time, () => time.GetUtcNow() - DateTimeOffset.UnixEpoch);
        TimeSpan start = time.GetUtcNow() - DateTimeOffset.UnixEpoch;

        ValueTask<bool> first = wake.WaitUntilAsync(
            start + TimeSpan.FromMilliseconds(10),
            CancellationToken.None
        );
        wake.Signal();
        Assert.IsTrue(await first.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        ValueTask<bool> second = wake.WaitAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(10));
        await Task.Yield();
        Assert.IsFalse(second.IsCompleted, "the first wait's deadline is not the second's");
        wake.Signal();
        Assert.IsTrue(await second.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task Wait_Cancelled_Throws()
    {
        WakeSignal wake = new();
        using CancellationTokenSource stop = new();
        ValueTask<bool> waiting = wake.WaitAsync(stop.Token);

        await stop.CancelAsync();

        _ = await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await waiting);
        _ = Assert.ThrowsExactly<OperationCanceledException>(() => wake.WaitAsync(stop.Token));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Wait_CancelledWhileItBegins_AlwaysEnds()
    {
        // A cancellation racing the wait's setup must still end it, wherever it lands. Each round races
        // one on another thread; a lost one hangs its round past the timeout.
        for (int round = 0; round < 5_000; round++)
        {
            WakeSignal wake = new();
            using CancellationTokenSource stop = new();
            var cancel = Task.Run(stop.Cancel);
            try
            {
                _ = await wake.WaitAsync(stop.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Fail("a wait with no signal ended without being cancelled");
            }
            catch (OperationCanceledException)
            {
                // The outcome every round must reach.
            }

            await cancel;
        }
    }

    [TestMethod]
    public async Task Waits_AllocateNothing()
    {
        FakeTimeProvider time = new();
        WakeSignal wake = new(time, () => time.GetUtcNow() - DateTimeOffset.UnixEpoch);
        using CancellationTokenSource stop = new();

        // Signalled and deadline waits, each taking the synchronous path once the signal is pending, as
        // a busy loop does.
        for (int warm = 0; warm < 2; warm++)
        {
            wake.Signal();
            _ = await wake.WaitUntilAsync(TimeSpan.MaxValue, stop.Token);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            wake.Signal();
            _ = await wake.WaitUntilAsync(TimeSpan.MaxValue, stop.Token);
            wake.Signal();
            _ = await wake.WaitAsync(stop.Token);
        }

        Assert.AreEqual(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
