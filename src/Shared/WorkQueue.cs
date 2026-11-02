namespace Agash.StreamTransport.Threading;

/// <summary>
/// Work handed to one loop: written from any thread, drained by the loop, which waits on a
/// <see cref="WakeSignal"/> when it finds nothing. Nothing is allocated once the queue's buffer has
/// grown to the most ever waiting: an unbounded channel allocates a segment every few dozen items, and
/// reading it through an async enumerator allocates on every wait that suspends.
/// </summary>
/// <typeparam name="T">The work.</typeparam>
internal sealed class WorkQueue<T>
{
    private readonly Queue<T> _items = new();
    private readonly WakeSignal _arrived = new();
    private bool _completed;

    /// <summary>Queues work, unless the queue has been completed.</summary>
    /// <param name="item">The work.</param>
    /// <returns>False once completed; the caller keeps the work then.</returns>
    public bool TryWrite(T item)
    {
        lock (_items)
        {
            if (_completed)
            {
                return false;
            }

            _items.Enqueue(item);
        }

        _arrived.Signal();
        return true;
    }

    /// <summary>Takes the oldest work.</summary>
    /// <param name="item">The work.</param>
    /// <returns>False when none is waiting.</returns>
    public bool TryRead(out T item)
    {
        lock (_items)
        {
            return _items.TryDequeue(out item!);
        }
    }

    /// <summary>Whether the queue was completed and all its work taken: the loop ends then.</summary>
    public bool IsFinished
    {
        get
        {
            lock (_items)
            {
                return _completed && _items.Count == 0;
            }
        }
    }

    /// <summary>Waits for work written after the loop last found the queue empty.</summary>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>A task that completes when work is written or the queue completed.</returns>
    public ValueTask<bool> WaitAsync(CancellationToken cancellationToken) =>
        _arrived.WaitAsync(cancellationToken);

    /// <summary>Takes no more work; what is queued is still read.</summary>
    public void Complete()
    {
        lock (_items)
        {
            _completed = true;
        }

        _arrived.Signal();
    }
}
