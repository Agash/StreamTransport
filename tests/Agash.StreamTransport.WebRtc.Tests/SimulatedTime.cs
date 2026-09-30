using Microsoft.Extensions.Time.Testing;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// Drives code on a <see cref="FakeTimeProvider"/>: simulated time advances in small steps, and between
/// steps the test yields briefly in real time so timer callbacks and in-memory deliveries run. The code
/// under test sees only simulated time, so production timings (a 30 s consent timeout) take a moment.
/// </summary>
internal sealed class SimulatedTime
{
    // Real time given to continuations after each step. Protocol timeouts are hundreds of steps long,
    // so a slow machine delays the test without changing what the code under test observes.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(2);

    public FakeTimeProvider Clock { get; } = new();

    /// <summary>The step simulated time advances by.</summary>
    public TimeSpan Step { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Advances simulated time by a duration, a step at a time.</summary>
    public async Task RunAsync(TimeSpan duration)
    {
        for (TimeSpan elapsed = TimeSpan.Zero; elapsed < duration; elapsed += Step)
        {
            Clock.Advance(Step);
            await Task.Delay(Settle);
        }
    }

    /// <summary>
    /// Advances simulated time until <paramref name="condition"/> holds, at most <paramref name="budget"/>;
    /// returns whether it held.
    /// </summary>
    public async Task<bool> RunUntilAsync(Func<bool> condition, TimeSpan budget)
    {
        for (TimeSpan elapsed = TimeSpan.Zero; elapsed <= budget; elapsed += Step)
        {
            if (condition())
            {
                return true;
            }

            Clock.Advance(Step);
            await Task.Delay(Settle);
        }

        return condition();
    }

    /// <inheritdoc cref="RunUntilAsync(Func{bool}, TimeSpan)"/>
    public Task<bool> RunUntilAsync(Task task, TimeSpan budget) =>
        RunUntilAsync(() => task.IsCompleted, budget);
}
