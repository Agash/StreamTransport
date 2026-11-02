using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.Sync;

/// <summary>
/// Where a session's decoded audio and video go: straight to their sinks, or, in synced playout, through
/// one scheduler by the sender's capture times so they lip-sync. A frame without a capture time plays on
/// arrival.
/// </summary>
internal sealed class Playout : IAsyncDisposable
{
    private readonly PlayoutScheduler? _scheduler;
    private readonly StreamTransportMetrics _metrics;
    private readonly SyncMonitor _sync;

    // Entries come back once played or dropped, so the frames in flight reuse a handful of them.
    private readonly Stack<VideoEntry> _videoEntries = new();
    private readonly Stack<AudioEntry> _audioEntries = new();
    private long _audioOffsetTicks;

    /// <summary>Playout as the options ask.</summary>
    /// <param name="options">The session options.</param>
    /// <param name="clock">The local media clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="metrics">The library's instruments.</param>
    public Playout(
        MediaSessionOptions options,
        MediaClock clock,
        ILogger logger,
        StreamTransportMetrics metrics
    )
    {
        _metrics = metrics;
        _sync = new SyncMonitor(clock, metrics);
        if (options.Playout == PlayoutMode.Synced)
        {
            MaxDelay = options.MaxPlayoutDelay;
            _scheduler = new PlayoutScheduler(
                new PlayoutTimeline(
                    options.MinPlayoutDelay,
                    options.MaxPlayoutDelay,
                    options.PlayoutMargin
                ),
                clock,
                logger
            );
        }
    }

    /// <summary>
    /// How much later audio plays than its slot: the video output path's latency less the audio
    /// output's, so the two reach the viewer together (#14). Negative plays audio earlier.
    /// </summary>
    public TimeSpan AudioOutputOffset
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _audioOffsetTicks));
        set => Interlocked.Exchange(ref _audioOffsetTicks, value.Ticks);
    }

    /// <summary>The longest a frame waits for its slot, or zero when frames play on arrival.</summary>
    public TimeSpan MaxDelay { get; }

    /// <summary>The measured lip sync, smoothed: positive when audio lags video; null until measured.</summary>
    public TimeSpan? AvSyncOffset => _sync.Offset;

    /// <summary>Pictures that went unshown because playout fell behind and a later one was due.</summary>
    public int VideoFramesLate => _scheduler?.VideoFramesDropped ?? 0;

    /// <summary>The playout buffer depth, or zero when frames play on arrival.</summary>
    public TimeSpan CurrentDelay => _scheduler?.CurrentDelay ?? TimeSpan.Zero;

    /// <summary>
    /// Keeps the playout delay within the range the sender asked for, as far as this session's own
    /// maximum allows; frames that play on arrival have no delay to bound.
    /// </summary>
    /// <param name="range">The sender's range.</param>
    public void Request(PlayoutDelayRange range) => _scheduler?.Bound(range.Minimum, range.Maximum);

    /// <summary>Plays a decoded video frame now or at its slot.</summary>
    /// <param name="frame">The frame, borrowed.</param>
    /// <param name="capture">The sender's capture instant, when known.</param>
    /// <param name="sink">Where it goes.</param>
    /// <param name="presented">Called once the frame has reached the sink, for a traced frame.</param>
    public void Video(
        in VideoFrame frame,
        NtpTime? capture,
        IVideoFrameConsumer sink,
        Action? presented = null
    )
    {
        if (_scheduler is { } scheduler && capture is { } at)
        {
            scheduler.Schedule(
                PlayoutStream.Video,
                at,
                RentVideo(frame.Retain(), sink, at, presented)
            );
            _metrics.PlayoutDelay.Record(scheduler.CurrentDelay.TotalSeconds);
        }
        else
        {
            sink.OnFrame(in frame);
            if (capture is { } shown)
            {
                _sync.VideoPresented(shown);
            }

            presented?.Invoke();
        }
    }

    /// <summary>Plays decoded audio now or at its slot.</summary>
    /// <param name="frame">The audio, borrowed.</param>
    /// <param name="capture">The sender's capture instant, when known.</param>
    /// <param name="sink">Where it goes.</param>
    public void Audio(in AudioFrame frame, NtpTime? capture, IAudioFrameConsumer sink)
    {
        if (_scheduler is { } scheduler && capture is { } at)
        {
            scheduler.Schedule(
                PlayoutStream.Audio,
                at,
                RentAudio(frame.Retain(), sink, at),
                AudioOutputOffset
            );
        }
        else
        {
            sink.OnFrame(in frame);
            if (capture is { } presented)
            {
                _sync.AudioPresented(presented);
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _scheduler?.DisposeAsync() ?? ValueTask.CompletedTask;

    private VideoEntry RentVideo(
        VideoFrameLease lease,
        IVideoFrameConsumer sink,
        NtpTime capture,
        Action? presented
    )
    {
        VideoEntry? entry;
        lock (_videoEntries)
        {
            _ = _videoEntries.TryPop(out entry);
        }

        entry ??= new VideoEntry(this);
        entry.Set(lease, sink, capture, presented);
        return entry;
    }

    private AudioEntry RentAudio(AudioFrameLease lease, IAudioFrameConsumer sink, NtpTime capture)
    {
        AudioEntry? entry;
        lock (_audioEntries)
        {
            _ = _audioEntries.TryPop(out entry);
        }

        entry ??= new AudioEntry(this);
        entry.Set(lease, sink, capture);
        return entry;
    }

    private sealed class VideoEntry(Playout owner) : IPlayoutEntry
    {
        private VideoFrameLease? _lease;
        private IVideoFrameConsumer? _sink;
        private NtpTime _capture;
        private Action? _presented;

        public void Set(
            VideoFrameLease lease,
            IVideoFrameConsumer sink,
            NtpTime capture,
            Action? presented
        )
        {
            _lease = lease;
            _sink = sink;
            _capture = capture;
            _presented = presented;
        }

        public void Play()
        {
            _sink!.OnFrame(_lease!.Frame);
            owner._sync.VideoPresented(_capture);
            _presented?.Invoke();
        }

        // Played or dropped, the entry goes back to its playout once its frame is released.
        public void Dispose()
        {
            _lease?.Dispose();
            _lease = null;
            _sink = null;
            _presented = null;
            lock (owner._videoEntries)
            {
                owner._videoEntries.Push(this);
            }
        }
    }

    private sealed class AudioEntry(Playout owner) : IPlayoutEntry
    {
        private AudioFrameLease? _lease;
        private IAudioFrameConsumer? _sink;
        private NtpTime _capture;

        public void Set(AudioFrameLease lease, IAudioFrameConsumer sink, NtpTime capture)
        {
            _lease = lease;
            _sink = sink;
            _capture = capture;
        }

        public void Play()
        {
            _sink!.OnFrame(_lease!.Frame);
            owner._sync.AudioPresented(_capture);
        }

        public void Dispose()
        {
            _lease?.Dispose();
            _lease = null;
            _sink = null;
            lock (owner._audioEntries)
            {
                owner._audioEntries.Push(this);
            }
        }
    }
}
