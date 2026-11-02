using Metal;

namespace Agash.StreamTransport.MacOS.Metal;

/// <summary>
/// Hands on the results of GPU work as its command buffers complete, in the order they were committed
/// and one at a time, so a stage delivers finished frames without a thread waiting for the GPU. Metal
/// calls completion handlers on threads of its own.
/// </summary>
internal sealed class CommandCompletions
{
    private readonly Lock _queue = new();
    private readonly Lock _delivery = new();
    private readonly Queue<Entry> _entries = new();

    /// <summary>Commits the work; <paramref name="then"/> runs once it and all work committed before it have completed.</summary>
    /// <param name="commands">The command buffer, not yet committed.</param>
    /// <param name="then">Called with the failure, or null when the work ran.</param>
    public void Commit(IMTLCommandBuffer commands, Action<string?> then)
    {
        Entry entry = new(then);
        lock (_queue)
        {
            _entries.Enqueue(entry);
        }

        commands.AddCompletedHandler(completed =>
        {
            entry.Failure =
                completed.Status == MTLCommandBufferStatus.Error
                    ? completed.Error?.LocalizedDescription ?? "the command buffer failed"
                    : null;
            Volatile.Write(ref entry.Done, true);
            Drain();
        });
        commands.Commit();
    }

    // Delivers every completed entry at the head, in order; one thread at a time.
    private void Drain()
    {
        lock (_delivery)
        {
            while (true)
            {
                Entry entry;
                lock (_queue)
                {
                    if (!_entries.TryPeek(out entry!) || !Volatile.Read(ref entry.Done))
                    {
                        return;
                    }

                    _ = _entries.Dequeue();
                }

                entry.Then(entry.Failure);
            }
        }
    }

    private sealed class Entry(Action<string?> then)
    {
        public readonly Action<string?> Then = then;
        public string? Failure;
        public bool Done;
    }
}
