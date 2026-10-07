using Agash.StreamTransport.Media;
using Agash.StreamTransport.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.Sync;

/// <summary>Something held for playout: an owned frame and where it goes.</summary>
internal interface IPlayoutEntry : IDisposable
{
    /// <summary>Hands the frame to its sink.</summary>
    void Play();
}

/// <summary>
/// Holds decoded audio and video in one queue ordered by release time and hands each entry to its sink
/// when it is due, so the stream that arrives first waits for the other and they lip-sync. Release times
/// come from a <see cref="PlayoutTimeline"/>; one loop sleeps on the injected clock until the next entry
/// is due or an earlier one arrives. Entries still queued at shutdown are released without playing.
/// </summary>
internal sealed partial class PlayoutScheduler : IAsyncDisposable
{
    private readonly PlayoutTimeline _timeline;
    private readonly MediaClock _clock;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly PriorityQueue<IPlayoutEntry, MediaTime> _queue = new();
    private readonly WakeSignal _wake;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    /// <summary>A scheduler releasing on a timeline.</summary>
    /// <param name="timeline">The timeline that sets release times.</param>
    /// <param name="clock">The local media clock.</param>
    /// <param name="logger">The logger.</param>
    public PlayoutScheduler(PlayoutTimeline timeline, MediaClock clock, ILogger? logger = null)
    {
        _timeline = timeline;
        _clock = clock;
        _logger = logger ?? NullLogger.Instance;
        _wake = new WakeSignal(clock.TimeProvider, () => clock.Now.ToTimeSpan());
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>The playout buffer depth now.</summary>
    public TimeSpan CurrentDelay
    {
        get
        {
            lock (_gate)
            {
                return _timeline.CurrentDelay;
            }
        }
    }

    /// <summary>
    /// Holds an entry until the frame captured at a sender instant is due, shifted by an extra delay
    /// (the difference between the output paths' own latencies, which may be negative).
    /// </summary>
    /// <param name="stream">The entry's stream.</param>
    /// <param name="capture">The sender's capture instant.</param>
    /// <param name="entry">The entry, which the scheduler now owns.</param>
    /// <param name="extraDelay">How much later than its slot the entry plays.</param>
    public void Schedule(
        PlayoutStream stream,
        NtpTime capture,
        IPlayoutEntry entry,
        TimeSpan extraDelay = default
    )
    {
        lock (_gate)
        {
            MediaTime release = _timeline.Release(stream, capture, _clock.Now) + extraDelay;
            _queue.Enqueue(entry, release);
        }

        _wake.Signal();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        lock (_gate)
        {
            while (_queue.TryDequeue(out IPlayoutEntry? entry, out _))
            {
                entry.Dispose();
            }
        }

        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                MediaTime? due = PlayDue();
                if (due is { } next)
                {
                    _ = await _wake
                        .WaitUntilAsync(next.ToTimeSpan(), cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Deliberately not logged: cancellation is how the scheduler stops.
        }
    }

    // Plays every entry that is due and returns when the next one is, or null when none is queued.
    private MediaTime? PlayDue()
    {
        while (true)
        {
            IPlayoutEntry? entry;
            lock (_gate)
            {
                if (!_queue.TryPeek(out entry, out MediaTime release))
                {
                    return null;
                }

                if (release > _clock.Now)
                {
                    return release;
                }

                _ = _queue.Dequeue();
            }

            using (entry)
            {
                try
                {
                    entry.Play();
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // A sink that throws loses that frame; the rest of the stream keeps playing.
                    LogPlayFailed(exception);
                }
            }
        }
    }

    [LoggerMessage(
        2030,
        LogLevel.Warning,
        "A sink failed to take a scheduled frame; it is dropped."
    )]
    private partial void LogPlayFailed(Exception exception);
}
