namespace Agash.StreamTransport.WebRtc.Tests;

// A counter a test waits on: Increment is called from the event under test, and UntilAsync completes when
// the count reaches a target, so no test has to poll.
internal sealed class CountWatch
{
    private readonly Lock _gate = new();
    private readonly List<(int Target, TaskCompletionSource Done)> _waiters = [];
    private int _count;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public void Increment()
    {
        List<TaskCompletionSource>? ready = null;
        lock (_gate)
        {
            _count++;
            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_count >= _waiters[i].Target)
                {
                    (ready ??= []).Add(_waiters[i].Done);
                    _waiters.RemoveAt(i);
                }
            }
        }

        if (ready is not null)
        {
            foreach (TaskCompletionSource done in ready)
            {
                _ = done.TrySetResult();
            }
        }
    }

    public Task UntilAsync(int target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_count >= target)
            {
                return Task.CompletedTask;
            }

            _waiters.Add((target, done));
        }

        return done.Task.WaitAsync(timeout, cancellationToken);
    }
}
