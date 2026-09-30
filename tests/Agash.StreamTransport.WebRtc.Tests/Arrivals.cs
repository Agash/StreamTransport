using System.Collections.Concurrent;

namespace Agash.StreamTransport.WebRtc.Tests;

// The RTP timestamps that arrived, each awaitable.
internal sealed class Arrivals
{
    private readonly ConcurrentDictionary<uint, TaskCompletionSource> _arrived = new();

    public void Record(uint timestamp) => Signal(timestamp).TrySetResult();

    public Task Of(uint timestamp) => Signal(timestamp).Task;

    private TaskCompletionSource Signal(uint timestamp) =>
        _arrived.GetOrAdd(
            timestamp,
            static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        );
}

// Sends at a steady cadence, as a media source does, until something is done or time runs out.
internal static class Cadence
{
    public static async Task SendUntilAsync(
        Func<ValueTask> send,
        Task done,
        TimeSpan period,
        TimeSpan timeout
    )
    {
        using CancellationTokenSource expiry = new(timeout);
        using PeriodicTimer timer = new(period);
        try
        {
            do
            {
                await send();
            } while (!done.IsCompleted && await timer.WaitForNextTickAsync(expiry.Token));
        }
        catch (OperationCanceledException)
        {
            // The timeout passed; the caller asserts on done.
        }
    }
}
