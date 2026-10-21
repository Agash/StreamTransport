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
/// Holds decoded audio and video until the frame captured at each sender instant is due, so the stream
/// that arrives first waits for the other and they lip-sync. Release times come from one shared
/// <see cref="PlayoutTimeline"/>; each stream plays from a lane of its own, a queue and a loop sleeping on
/// the injected clock, so a sink slow to take a picture never holds back the sound. A video lane that
/// falls behind shows the latest due picture and lets the older ones go unplayed, as libwebrtc's render
/// queue and GStreamer's late-buffer QoS do; audio is never skipped. Entries still queued at shutdown are
/// released without playing.
/// </summary>
internal sealed partial class PlayoutScheduler : IAsyncDisposable
{
    private readonly PlayoutTimeline _timeline;
    private readonly MediaClock _clock;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Lane _audio;
    private readonly Lane _video;
    private int _videoDropped;

    /// <summary>A scheduler releasing on a timeline.</summary>
    /// <param name="timeline">The timeline that sets release times.</param>
    /// <param name="clock">The local media clock.</param>
    /// <param name="logger">The logger.</param>
    public PlayoutScheduler(PlayoutTimeline timeline, MediaClock clock, ILogger? logger = null)
    {
        _timeline = timeline;
        _clock = clock;
        _logger = logger ?? NullLogger.Instance;
        _audio = new Lane(this, PlayoutStream.Audio);
        _video = new Lane(this, PlayoutStream.Video);
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

    /// <summary>Pictures that came due while a later one was due too, and went unplayed.</summary>
    public int VideoFramesDropped => Volatile.Read(ref _videoDropped);

    /// <summary>Bounds the buffer to a sender's playout delay range, within this side's maximum.</summary>
    /// <param name="minimum">The least delay asked for.</param>
    /// <param name="maximum">The most delay asked for.</param>
    public void Bound(TimeSpan minimum, TimeSpan maximum)
    {
        lock (_gate)
        {
            _timeline.Bound(minimum, maximum);
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
        MediaTime release;
        lock (_gate)
        {
            release = _timeline.Release(stream, capture, _clock.Now) + extraDelay;
        }

        (stream == PlayoutStream.Video ? _video : _audio).Enqueue(
            entry,
            new Slot(release, capture)
        );
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _audio.DisposeAsync().ConfigureAwait(false);
        await _video.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    // One stream's queue and the loop that plays it.
    private sealed class Lane : IAsyncDisposable
    {
        private readonly PlayoutScheduler _owner;
        private readonly PlayoutStream _stream;
        private readonly Lock _gate = new();
        private readonly PriorityQueue<IPlayoutEntry, Slot> _queue = new();
        private readonly WakeSignal _wake;
        private readonly Task _loop;

        // The last picture shown, so a later-arriving earlier one is not shown after it.
        private MediaTime _lastShown = new(long.MinValue);

        public Lane(PlayoutScheduler owner, PlayoutStream stream)
        {
            _owner = owner;
            _stream = stream;
            MediaClock clock = owner._clock;
            _wake = new WakeSignal(clock.TimeProvider, () => clock.Now.ToTimeSpan());
            _loop = Task.Run(() => RunAsync(owner._stop.Token));
        }

        public void Enqueue(IPlayoutEntry entry, Slot slot)
        {
            lock (_gate)
            {
                // libwebrtc's render queue (VideoRenderFrames::AddFrame): a picture already more than
                // half a second past its slot is dropped while others wait, so a backlog drains rather than
                // plays out late; with none waiting it still shows, so a slow machine shows something.
                if (
                    _stream == PlayoutStream.Video
                    && _queue.Count > 0
                    && slot.Release + StaleAfter < _owner._clock.Now
                )
                {
                    entry.Dispose();
                    _owner.NoteVideoBehind(1);
                    return;
                }

                _queue.Enqueue(entry, slot);
            }

            _wake.Signal();
        }

        public async ValueTask DisposeAsync()
        {
            await _loop.ConfigureAwait(false);
            lock (_gate)
            {
                while (_queue.TryDequeue(out IPlayoutEntry? entry, out _))
                {
                    entry.Dispose();
                }
            }
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
                int superseded = 0;
                lock (_gate)
                {
                    if (!_queue.TryPeek(out entry, out Slot slot))
                    {
                        return null;
                    }

                    MediaTime now = _owner._clock.Now;
                    if (slot.Release > now)
                    {
                        return slot.Release;
                    }

                    _ = _queue.Dequeue();

                    // Of the pictures due now, only the latest is shown: the rest are already late
                    // (VideoRenderFrames::FrameToRender).
                    while (
                        _stream == PlayoutStream.Video
                        && _queue.TryPeek(out IPlayoutEntry? later, out Slot laterSlot)
                        && laterSlot.Release <= now
                    )
                    {
                        entry.Dispose();
                        entry = _queue.Dequeue();
                        slot = laterSlot;
                        superseded++;
                    }

                    // A picture whose slot is before the last one shown would go backwards in time.
                    if (_stream == PlayoutStream.Video)
                    {
                        if (slot.Release < _lastShown)
                        {
                            entry.Dispose();
                            superseded++;
                            entry = null;
                        }
                        else
                        {
                            _lastShown = slot.Release;
                        }
                    }
                }

                if (entry is null)
                {
                    _owner.NoteVideoBehind(superseded);
                    continue;
                }

                if (superseded > 0)
                {
                    _owner.NoteVideoBehind(superseded);
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
                        _owner.LogPlayFailed(exception);
                    }
                }
            }
        }
    }

    private void NoteVideoBehind(int dropped)
    {
        _ = Interlocked.Add(ref _videoDropped, dropped);
        LogVideoBehind(dropped);
    }

    // How late past its slot a picture may arrive and still queue behind others.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMilliseconds(500);

    // When an entry is released, and, among entries released together, the capture order.
    private readonly record struct Slot(MediaTime Release, NtpTime Capture) : IComparable<Slot>
    {
        public int CompareTo(Slot other)
        {
            int byRelease = Release.CompareTo(other.Release);
            return byRelease != 0 ? byRelease : Capture.Value.CompareTo(other.Capture.Value);
        }
    }

    [LoggerMessage(
        2030,
        LogLevel.Warning,
        "A sink failed to take a scheduled frame; it is dropped."
    )]
    private partial void LogPlayFailed(Exception exception);

    [LoggerMessage(
        2031,
        LogLevel.Debug,
        "Video playout fell behind: {Dropped} late pictures went unplayed for a later one."
    )]
    private partial void LogVideoBehind(int dropped);
}
