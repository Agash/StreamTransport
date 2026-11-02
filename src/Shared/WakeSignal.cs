using System.Threading.Tasks.Sources;

namespace Agash.StreamTransport.Threading;

/// <summary>
/// Wakes one waiting loop. Signals coalesce: signalling an already signalled wake-up does nothing, and
/// the loop then drains whatever work is queued. The loop sleeps until signalled or until a deadline on
/// its own clock; the deadline is absolute, so time that passes before the wait begins counts.
/// </summary>
/// <remarks>
/// One loop waits at a time. A wait allocates nothing: the wake-up is its own reusable value task
/// source, its deadline timer is created once and re-armed, and the cancellation registration is kept
/// for as long as the loop waits with the same token. A deadline ends a wait with false, not with an
/// exception.
/// </remarks>
internal sealed class WakeSignal : IValueTaskSource<bool>, IThreadPoolWorkItem
{
    private const int Idle = 0;
    private const int Pending = 1;
    private const int Waiting = 2;

    private readonly TimeProvider? _time;
    private readonly Func<TimeSpan>? _now;

    // Continuations run on the pool, not on the thread that signals: a signal comes from the network or
    // a capture thread, which must not run the loop. The signal queues this object, a pool work item,
    // which completes the wait there: a source that runs its continuations asynchronously would queue a
    // new work item for each.
    private ManualResetValueTaskSourceCore<bool> _core = new()
    {
        RunContinuationsAsynchronously = false,
    };
    private ITimer? _timer;
    private CancellationToken _registeredFor;
    private CancellationTokenRegistration _registration;
    private int _armedFor = -1;
    private int _state;

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
    public void Signal()
    {
        while (true)
        {
            int state = Volatile.Read(ref _state);
            if (state == Pending)
            {
                return;
            }

            if (state == Waiting)
            {
                if (Interlocked.CompareExchange(ref _state, Idle, Waiting) == Waiting)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
                    return;
                }
            }
            else if (Interlocked.CompareExchange(ref _state, Pending, Idle) == Idle)
            {
                return;
            }
        }
    }

    /// <summary>Waits for a signal.</summary>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>A task that completes, with true, when signalled.</returns>
    public ValueTask<bool> WaitAsync(CancellationToken cancellationToken) =>
        Wait(deadline: null, Timeout.InfiniteTimeSpan, cancellationToken);

    /// <summary>Waits for a signal or for a deadline, whichever comes first.</summary>
    /// <param name="deadline">When to stop waiting, on the loop's clock.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>True when signalled, false when the deadline passed.</returns>
    /// <exception cref="InvalidOperationException">The signal was made without a clock.</exception>
    public ValueTask<bool> WaitUntilAsync(TimeSpan deadline, CancellationToken cancellationToken)
    {
        if (_time is null)
        {
            throw new InvalidOperationException("This wake-up has no clock for deadlines.");
        }

        TimeSpan remaining = deadline - _now!();
        return remaining <= TimeSpan.Zero
            ? new ValueTask<bool>(TakePending())
            : Wait(deadline, remaining, cancellationToken);
    }

    // A signal's completion, on the pool.
    void IThreadPoolWorkItem.Execute() => _core.SetResult(true);

    bool IValueTaskSource<bool>.GetResult(short token) => _core.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<bool>.OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags
    ) => _core.OnCompleted(continuation, state, token, flags);

    // A pending signal is taken at once; otherwise the loop waits, with a deadline when one is given.
    private ValueTask<bool> Wait(
        TimeSpan? deadline,
        TimeSpan remaining,
        CancellationToken cancellationToken
    )
    {
        if (TakePending())
        {
            return new ValueTask<bool>(true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _core.Reset();
        if (cancellationToken.CanBeCanceled && cancellationToken != _registeredFor)
        {
            _registration.Dispose();
            _registeredFor = cancellationToken;
            _registration = cancellationToken.UnsafeRegister(
                static (state, token) => ((WakeSignal)state!).Cancel(token),
                this
            );
        }

        if (Interlocked.CompareExchange(ref _state, Waiting, Idle) != Idle)
        {
            // A signal came in between the check and the wait.
            _state = Idle;
            return new ValueTask<bool>(true);
        }

        // A cancellation before the wait was set found no wait to end; the token says.
        if (
            cancellationToken.IsCancellationRequested
            && Interlocked.CompareExchange(ref _state, Idle, Waiting) == Waiting
        )
        {
            return ValueTask.FromCanceled<bool>(cancellationToken);
        }

        if (remaining != Timeout.InfiniteTimeSpan)
        {
            _timer ??= _time!.CreateTimer(
                static state => ((WakeSignal)state!).Expire(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan
            );
            Volatile.Write(ref _armedFor, _core.Version);
            _ = _timer.Change(remaining, Timeout.InfiniteTimeSpan);

            // Time that passed while the timer was being armed is not in its due time; the clock says.
            if (
                _now!() >= deadline
                && Interlocked.CompareExchange(ref _state, Idle, Waiting) == Waiting
            )
            {
                _ = _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return new ValueTask<bool>(false);
            }
        }

        return new ValueTask<bool>(this, _core.Version);
    }

    private bool TakePending() => Interlocked.CompareExchange(ref _state, Idle, Pending) == Pending;

    // The timer armed for an earlier wait, which a signal ended, does not end a later one.
    private void Expire()
    {
        if (
            Volatile.Read(ref _armedFor) == _core.Version
            && Interlocked.CompareExchange(ref _state, Idle, Waiting) == Waiting
        )
        {
            _core.SetResult(false);
        }
    }

    private void Cancel(CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _state, Idle, Waiting) == Waiting)
        {
            // On the pool too: the thread that cancels may be holding locks while it stops the loop.
            ThreadPool.UnsafeQueueUserWorkItem(
                static state =>
                    state.Signal._core.SetException(new OperationCanceledException(state.Token)),
                (Signal: this, Token: token),
                preferLocal: false
            );
        }
    }
}
