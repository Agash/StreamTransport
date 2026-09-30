using System.Threading.Channels;

namespace Agash.StreamTransport.Threading;

/// <summary>
/// Wakes one waiting loop. Signals coalesce: signalling an already signalled wake-up does nothing, and
/// the loop then drains whatever work is queued. The loop sleeps until signalled or until a deadline on
/// its own clock; the deadline is absolute, so time that passes before the wait begins counts.
/// </summary>
internal sealed class WakeSignal
{
    private readonly TimeProvider? _time;
    private readonly Func<TimeSpan>? _now;

    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        }
    );

    /// <summary>A wake-up for a loop that only waits for signals.</summary>
    public WakeSignal() { }

    /// <summary>A wake-up for a loop that also waits for deadlines.</summary>
    /// <param name="timeProvider">The clock the deadline's timer runs on.</param>
    /// <param name="now">The loop's clock now, which deadlines are on.</param>
    public WakeSignal(TimeProvider timeProvider, Func<TimeSpan> now)
    {
        _time = timeProvider;
        _now = now;
    }

    /// <summary>Wakes the loop, or leaves the wake-up pending until it next waits.</summary>
    public void Signal() => _ = _channel.Writer.TryWrite(true);

    /// <summary>Waits for a signal.</summary>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>A task that completes when signalled.</returns>
    public async ValueTask WaitAsync(CancellationToken cancellationToken) =>
        _ = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Waits for a signal or for a deadline, whichever comes first.</summary>
    /// <param name="deadline">When to stop waiting, on the loop's clock.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>True when signalled, false when the deadline passed.</returns>
    /// <exception cref="InvalidOperationException">The signal was made without a clock.</exception>
    public async ValueTask<bool> WaitUntilAsync(
        TimeSpan deadline,
        CancellationToken cancellationToken
    )
    {
        TimeProvider time =
            _time
            ?? throw new InvalidOperationException("This wake-up has no clock for deadlines.");
        Func<TimeSpan> now = _now!;
        if (_channel.Reader.TryRead(out _))
        {
            return true;
        }

        TimeSpan remaining = deadline - now();
        if (remaining <= TimeSpan.Zero)
        {
            return false;
        }

        using CancellationTokenSource elapsed = new(remaining, time);

        // Time that passed while the timer was being armed is not in its due time; the clock says.
        if (now() >= deadline)
        {
            return false;
        }

        using var either = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            elapsed.Token
        );
        try
        {
            _ = await _channel.Reader.ReadAsync(either.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Deliberately not logged: the deadline passing is the normal way this wait ends.
            return false;
        }
    }
}
