using System.Diagnostics;

namespace Agash.StreamTransport.Sync;

/// <summary>
/// Holds decoded audio and video frames in one release-time-ordered queue and presents each to its sink at its
/// scheduled local time, so the earlier-arriving stream (audio) is delayed to lip-sync with the
/// later stream (video). Release times come from <see cref="PlayoutTimeline"/> over the sender wall clock, so
/// both streams share one timeline. A background pump sleeps until the next frame is due, then submits it.
///
/// Engaged only when both audio and video are present and aligned (the receiver routes frames here only then);
/// single-stream and pre-alignment frames are presented on arrival by the caller, never held. The wall clock
/// is injectable for deterministic tests.
/// </summary>
internal sealed class PlayoutScheduler : IAsyncDisposable
{
    private readonly PlayoutTimeline _timeline;
    private readonly Func<long> _nowNs;
    private readonly Lock _gate = new();
    private readonly PriorityQueue<Action, long> _queue = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;

    /// <summary>Create a scheduler whose buffer depth adapts between <paramref name="minDelayNs"/> and <paramref name="maxDelayNs"/> (plus <paramref name="marginNs"/> headroom) to measured jitter.</summary>
    public PlayoutScheduler(long minDelayNs, long maxDelayNs, long marginNs, Func<long>? nowNs = null)
    {
        _timeline = new PlayoutTimeline(minDelayNs, maxDelayNs, marginNs);
        _nowNs = nowNs ?? DefaultNowNs;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Create a scheduler with a fixed buffer depth (min = max, no margin); used by tests.</summary>
    public PlayoutScheduler(long fixedDelayNs, Func<long>? nowNs = null)
        : this(fixedDelayNs, fixedDelayNs, 0, nowNs)
    {
    }

    /// <summary>Schedule <paramref name="submit"/> to run when the frame captured at <paramref name="senderWallNs"/> is due.</summary>
    public void Schedule(long senderWallNs, Action submit)
    {
        long releaseNs = _timeline.ReleaseLocalNs(senderWallNs, _nowNs());
        Enqueue(submit, releaseNs);
    }

    /// <summary>
    /// Feed a presented-on-arrival frame's capture time into the timeline (updates the clock-offset/jitter
    /// estimates) <i>without</i> queuing it. The asymmetric GPU-sync path calls this for each video frame - which
    /// it presents immediately because the GPU output texture cannot be held - so the timeline tracks video's
    /// slower arrival curve, and <see cref="ScheduleOnTimeline"/> lands audio on it.
    /// </summary>
    public void ObserveArrival(long senderWallNs) => _timeline.ObserveArrival(senderWallNs, _nowNs());

    /// <summary>The current adaptive jitter-buffer/playout depth in ns (for receive-side network telemetry).</summary>
    public long CurrentDelayNs => _timeline.CurrentDelayNs;

    /// <summary>
    /// Schedule <paramref name="submit"/> using the timeline's current estimates <i>without</i> updating them
    /// (see <see cref="PlayoutTimeline.PeekReleaseLocalNs"/>) - so the scheduled stream (audio) aligns to the
    /// arrival curve learned from <see cref="ObserveArrival"/> (video) and never pulls the offset to its own
    /// faster path.
    /// </summary>
    public void ScheduleOnTimeline(long senderWallNs, Action submit)
    {
        long releaseNs = _timeline.PeekReleaseLocalNs(senderWallNs);
        Enqueue(submit, releaseNs);
    }

    private void Enqueue(Action submit, long releaseNs)
    {
        lock (_gate)
        {
            _queue.Enqueue(submit, releaseNs);
        }

        _wake.Release(); // re-evaluate the next due time (this frame may be earlier than the current sleep).
    }

    private async Task PumpAsync()
    {
        CancellationToken ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            long? dueNs = PeekNextReleaseNs();
            if (dueNs is null)
            {
                // Nothing queued; wait until something is scheduled.
                try
                {
                    await _wake.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            long waitMs = (dueNs.Value - _nowNs()) / 1_000_000;
            if (waitMs > 0)
            {
                // Sleep until the frame is due, but wake early if a sooner frame arrives. Cap the wait so a
                // clock/anchor anomaly can't park the pump indefinitely.
                try
                {
                    await _wake.WaitAsync((int)Math.Min(waitMs, 1000), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            ReleaseDueFrames();
        }

        // Drain anything still queued so no frame is silently dropped on shutdown.
        ReleaseAllRemaining();
    }

    private long? PeekNextReleaseNs()
    {
        lock (_gate)
        {
            return _queue.TryPeek(out _, out long priority) ? priority : null;
        }
    }

    private void ReleaseDueFrames()
    {
        long now = _nowNs();
        while (true)
        {
            Action? submit = null;
            lock (_gate)
            {
                if (_queue.TryPeek(out _, out long releaseNs) && releaseNs <= now)
                {
                    submit = _queue.Dequeue();
                }
            }

            if (submit is null)
            {
                return;
            }

            submit();
        }
    }

    private void ReleaseAllRemaining()
    {
        while (true)
        {
            Action? submit = null;
            lock (_gate)
            {
                if (_queue.Count > 0)
                {
                    submit = _queue.Dequeue();
                }
            }

            if (submit is null)
            {
                return;
            }

            submit();
        }
    }

    private static long DefaultNowNs() => Stopwatch.GetTimestamp() * (1_000_000_000L / Stopwatch.Frequency);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _wake.Release();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation.
        }

        _cts.Dispose();
        _wake.Dispose();
    }
}
