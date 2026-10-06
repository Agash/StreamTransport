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

    /// <summary>The playout buffer depth, or zero when frames play on arrival.</summary>
    public TimeSpan CurrentDelay => _scheduler?.CurrentDelay ?? TimeSpan.Zero;

    /// <summary>Plays a decoded video frame now or at its slot.</summary>
    /// <param name="frame">The frame, borrowed.</param>
    /// <param name="capture">The sender's capture instant, when known.</param>
    /// <param name="sink">Where it goes.</param>
    public void Video(in VideoFrame frame, NtpTime? capture, IVideoFrameConsumer sink)
    {
        if (_scheduler is { } scheduler && capture is { } at)
        {
            scheduler.Schedule(at, new VideoEntry(frame.Retain(), sink));
            _metrics.PlayoutDelay.Record(scheduler.CurrentDelay.TotalSeconds);
        }
        else
        {
            sink.OnFrame(in frame);
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
            scheduler.Schedule(at, new AudioEntry(frame.Retain(), sink), AudioOutputOffset);
        }
        else
        {
            sink.OnFrame(in frame);
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _scheduler?.DisposeAsync() ?? ValueTask.CompletedTask;

    private sealed class VideoEntry(VideoFrameLease lease, IVideoFrameConsumer sink) : IPlayoutEntry
    {
        public void Play() => sink.OnFrame(lease.Frame);

        public void Dispose() => lease.Dispose();
    }

    private sealed class AudioEntry(AudioFrameLease lease, IAudioFrameConsumer sink) : IPlayoutEntry
    {
        public void Play() => sink.OnFrame(lease.Frame);

        public void Dispose() => lease.Dispose();
    }
}
