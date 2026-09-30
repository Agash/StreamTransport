using System.Threading.Channels;

namespace Agash.StreamTransport.Threading;

/// <summary>
/// Wakes one waiting loop. Signals coalesce: signalling an already signalled wake-up does nothing, and
/// the loop then drains whatever work is queued. The loop sleeps until signalled or until a timeout on
/// the injected clock.
/// </summary>
internal sealed class WakeSignal(TimeProvider timeProvider)
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        }
    );

    /// <summary>Wakes the loop, or leaves the wake-up pending until it next waits.</summary>
    public void Signal() => _ = _channel.Writer.TryWrite(true);

    /// <summary>Waits for a signal.</summary>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>A task that completes when signalled.</returns>
    public async ValueTask WaitAsync(CancellationToken cancellationToken) =>
        _ = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Waits for a signal or for a timeout, whichever comes first.</summary>
    /// <param name="timeout">How long to wait at most.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>True when signalled, false when the timeout elapsed.</returns>
    public async ValueTask<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_channel.Reader.TryRead(out _))
        {
            return true;
        }

        using CancellationTokenSource elapsed = new(timeout, timeProvider);
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
            // Deliberately not logged: the timeout elapsing is the normal way this wait ends.
            return false;
        }
    }
}
