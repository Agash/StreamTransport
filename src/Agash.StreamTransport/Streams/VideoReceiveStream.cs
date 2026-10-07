using System.Collections.Immutable;
using System.Threading.Channels;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.Sync;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.Streams;

/// <summary>What a video receive stream is built from.</summary>
/// <param name="Format">The negotiated codec and its parameters.</param>
/// <param name="Alpha">How the sender laid out transparency.</param>
/// <param name="RequestKeyframe">Asks the sender for a keyframe; the transport combines requests.</param>
internal sealed record VideoReceiveSetup(
    VideoCodecFormat Format,
    AlphaLayout Alpha,
    Action RequestKeyframe
);

/// <summary>Where a receive stream's timing frames go, and the clocks that place them.</summary>
/// <param name="WallClock">This side's wall clock, for receive, decode and presentation stamps.</param>
/// <param name="SenderClockOffset">This side's clock less the sender's, when estimated.</param>
/// <param name="Reported">Takes each timing frame's report once the frame is presented.</param>
internal sealed record VideoTimingSetup(
    CaptureClock WallClock,
    Func<TimeSpan?> SenderClockOffset,
    Action<VideoFrameTimingReport> Reported
);

/// <summary>
/// Receives one video stream from a peer: the transport hands over complete frames, and a worker
/// decodes, converts what the sink cannot take (and unpacks alpha), and plays each frame on arrival or
/// at its synced slot.
/// </summary>
internal sealed partial class VideoReceiveStream : IVideoFrameConsumer, IAsyncDisposable
{
    // Complete frames waiting for the decoder beyond which it is behind: a decoder or output that cannot
    // keep up drops what waits and resumes at a keyframe, so latency stays bounded instead of growing.
    private const int MaxBacklog = 8;

    private readonly VideoReceiveSetup _setup;
    private readonly IVideoSink _sink;
    private readonly MediaCodecRegistry _registry;
    private readonly Playout _playout;
    private readonly TimeProvider _time;
    private readonly MediaClock _clock;
    private readonly StreamTransportMetrics _metrics;
    private readonly ILogger _logger;
    private readonly ArrivalStamps _stamps;
    private readonly VideoTimingSetup? _timingSetup;

    // Timing frames between arrival and presentation, by the stamp their frame carries; a frame that is
    // skipped or fails takes its entry with it.
    private readonly Lock _timingGate = new();
    private readonly Dictionary<MediaTimestamp, PendingTiming> _timings = [];

    private readonly Channel<(
        EncodedFrameBuffer Frame,
        bool Keyframe,
        MediaTimestamp Stamp
    )> _frames = Channel.CreateUnbounded<(EncodedFrameBuffer, bool, MediaTimestamp)>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly Presenter _presenter;
    private IVideoDecoder? _decoder;
    private int _framesDecoded;
    private int _framesFailed;
    private int _framesSkipped;
    private bool _awaitingKeyframe;
    private int _waiting;

    /// <summary>Starts the decode worker.</summary>
    /// <param name="setup">The negotiated format and keyframe request.</param>
    /// <param name="sink">Where frames go.</param>
    /// <param name="registry">Where the decoder and processors come from.</param>
    /// <param name="playout">The session's playout.</param>
    /// <param name="clock">The local media clock.</param>
    /// <param name="metrics">The library's instruments.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timing">Where timing frames go; none traced when null.</param>
    public VideoReceiveStream(
        VideoReceiveSetup setup,
        IVideoSink sink,
        MediaCodecRegistry registry,
        Playout playout,
        MediaClock clock,
        StreamTransportMetrics metrics,
        ILogger logger,
        VideoTimingSetup? timing = null
    )
    {
        _setup = setup;
        _timingSetup = timing;
        _sink = sink;
        _registry = registry;
        _playout = playout;
        _metrics = metrics;
        _time = clock.TimeProvider;
        _clock = clock;
        _logger = logger;
        _stamps = new ArrivalStamps(clock);
        _presenter = new Presenter(this);
        _worker = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>Frames decoded.</summary>
    public int FramesDecoded => Volatile.Read(ref _framesDecoded);

    /// <summary>Frames the decoder rejected, usually for a loss inside them that was not repaired.</summary>
    public int FramesFailed => Volatile.Read(ref _framesFailed);

    /// <summary>Frames dropped undecoded because the decoder or the output fell behind.</summary>
    public int FramesSkipped => Volatile.Read(ref _framesSkipped);

    /// <summary>Takes a complete frame from the transport, on its receive thread; the stream owns it.</summary>
    /// <param name="frame">The access unit.</param>
    /// <param name="keyframe">Whether it can be decoded on its own.</param>
    /// <param name="capture">When the sender captured it, when known.</param>
    /// <param name="timing">The frame's timing, when the sender timed it.</param>
    public void OnEncodedFrame(
        EncodedFrameBuffer frame,
        bool keyframe,
        NtpTime? capture,
        VideoReceiveTiming? timing = null
    )
    {
        MediaTimestamp stamp = _stamps.Stamp(capture);
        if (timing is { } received && capture is { } captured && _timingSetup is not null)
        {
            lock (_timingGate)
            {
                // Bounded: a frame that never presents (dropped, failed) leaves an entry behind.
                if (_timings.Count > 64)
                {
                    _timings.Clear();
                }

                _timings[stamp] = new PendingTiming(received, captured);
            }
        }

        if (_frames.Writer.TryWrite((frame, keyframe, stamp)))
        {
            Interlocked.Increment(ref _waiting);
        }
        else
        {
            frame.Dispose();
        }
    }

    /// <summary>
    /// A decoded frame goes to playout as decoded; it is converted for the sink when it is shown, straight
    /// into the sink's own surface where the sink lends one, so nothing is converted that is not shown.
    /// </summary>
    /// <param name="frame">The decoded frame.</param>
    public void OnFrame(in VideoFrame frame)
    {
        Action? presented = null;
        if (_timingSetup is { } setup)
        {
            lock (_timingGate)
            {
                if (_timings.Remove(frame.Timestamp, out PendingTiming pending))
                {
                    presented = () => Report(setup, pending);
                }
            }
        }

        _playout.Video(in frame, _stamps.TakeCapture(frame.Timestamp), _presenter, presented);
    }

    private void Report(VideoTimingSetup setup, PendingTiming pending)
    {
        NtpTime now = setup.WallClock.ToNtp(_clock.Now);
        VideoReceiveTiming t = pending.Received;
        setup.Reported(
            new VideoFrameTimingReport(
                t.Reasons,
                pending.Capture,
                t.EncodeStart,
                t.EncodeFinish,
                t.PacketizationFinish,
                t.PacerExit,
                t.FirstPacketReceived,
                t.LastPacketReceived,
                pending.DecodeStart,
                pending.DecodeFinish,
                now,
                setup.SenderClockOffset()
            )
        );
    }

    // A timing frame's stamps so far: what arrived with it, then its decoding.
    private struct PendingTiming(VideoReceiveTiming received, NtpTime capture)
    {
        public VideoReceiveTiming Received { get; } = received;

        public NtpTime Capture { get; } = capture;

        public NtpTime DecodeStart { get; set; }

        public NtpTime DecodeFinish { get; set; }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _frames.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _worker.ConfigureAwait(false);
        while (_frames.Reader.TryRead(out (EncodedFrameBuffer Frame, bool, MediaTimestamp) left))
        {
            left.Frame.Dispose();
        }

        _presenter.Dispose();
        _decoder?.Dispose();
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (
                (EncodedFrameBuffer frame, bool keyframe, MediaTimestamp stamp) in _frames
                    .Reader.ReadAllAsync(cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                int waiting = Interlocked.Decrement(ref _waiting);
                using (frame)
                {
                    if (_awaitingKeyframe && !keyframe)
                    {
                        Skip(stamp);
                        continue;
                    }

                    _awaitingKeyframe = false;
                    if (waiting >= MaxBacklog)
                    {
                        CatchUp(stamp, waiting);
                        continue;
                    }

                    Decode(frame, keyframe, stamp);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Deliberately not logged: cancellation is how the stream stops.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogWorkerFailed(exception);
        }
    }

    // Drops this frame and every one waiting, and resumes at the next keyframe, asking for one now.
    private void CatchUp(MediaTimestamp stamp, int backlog)
    {
        Skip(stamp);
        while (
            _frames.Reader.TryRead(
                out (EncodedFrameBuffer Frame, bool Keyframe, MediaTimestamp Stamp) waiting
            )
        )
        {
            Interlocked.Decrement(ref _waiting);
            waiting.Frame.Dispose();
            Skip(waiting.Stamp);
        }

        _awaitingKeyframe = true;
        LogBehind(backlog + 1);
        RequestKeyframe();
    }

    private void Skip(MediaTimestamp stamp)
    {
        Interlocked.Increment(ref _framesSkipped);
        _metrics.VideoFramesSkipped.Add(1, StreamTransportMetrics.Codec(_setup.Format.Codec));
        _ = _stamps.TakeCapture(stamp);
    }

    private void Decode(EncodedFrameBuffer frame, bool keyframe, MediaTimestamp stamp)
    {
        try
        {
            _decoder ??= CreateDecoder();
            MediaTime started = _clock.Now;
            NoteDecode(stamp, started, finished: false);
            _decoder.Decode(
                new EncodedVideoFrame(frame.Span, _setup.Format.Codec, keyframe, stamp),
                this
            );
            NoteDecode(stamp, _clock.Now, finished: true);
            _metrics.VideoDecodeDuration.Record(
                (_clock.Now - started).TotalSeconds,
                StreamTransportMetrics.Codec(_setup.Format.Codec)
            );
            Interlocked.Increment(ref _framesDecoded);
            _metrics.VideoFramesDecoded.Add(1, StreamTransportMetrics.Codec(_setup.Format.Codec));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A frame the decoder rejects loses the picture until the next keyframe, so one is asked for.
            Interlocked.Increment(ref _framesFailed);
            _metrics.VideoFramesFailed.Add(1, StreamTransportMetrics.Codec(_setup.Format.Codec));
            LogDecodeFailed(exception);
            _ = _stamps.TakeCapture(stamp);
            RequestKeyframe();
        }
    }

    // Decode stamps of a timing frame. A decoder that hands the frame over during Decode reaches OnFrame
    // before the finish is noted, so the finish is taken there too.
    private void NoteDecode(MediaTimestamp stamp, MediaTime at, bool finished)
    {
        if (_timingSetup is not { } setup)
        {
            return;
        }

        lock (_timingGate)
        {
            if (_timings.TryGetValue(stamp, out PendingTiming pending))
            {
                NtpTime wall = setup.WallClock.ToNtp(at);
                if (finished)
                {
                    pending.DecodeFinish = wall;
                }
                else
                {
                    pending.DecodeStart = wall;
                    pending.DecodeFinish = wall;
                }

                _timings[stamp] = pending;
            }
        }
    }

    // Senders cap at 60 frames a second by default; playout's frame count is reckoned at that rate.
    private const double MaxFrameRate = 60;

    // The frames playout keeps while they wait for their slots, at most: its longest wait at the highest
    // frame rate sent, and the one being shown.
    private int HeldFrames => (int)Math.Ceiling(_playout.MaxDelay.TotalSeconds * MaxFrameRate) + 1;

    private bool NeedsProcessing(VideoStreamDescription description) =>
        _setup.Alpha == AlphaLayout.PackSideBySide
        || !_sink.Constraints.Storages.Contains(description.Storage)
        || !_sink.Constraints.PixelFormats.Contains(description.PixelFormat)
        || (_sink.Constraints.Device is { } device && description.Device != device);

    private IVideoProcessor CreateProcessor(VideoStreamDescription description)
    {
        AlphaLayout alpha =
            _setup.Alpha == AlphaLayout.PackSideBySide
                ? AlphaLayout.UnpackSideBySide
                : AlphaLayout.None;
        return _registry.TryCreateVideoProcessor(
            description,
            new VideoProcessing(_sink.Constraints, Alpha: alpha),
            out IVideoProcessor? processor
        )
            ? processor
            : throw new InvalidOperationException(
                $"No registered processor turns decoded {description} into what the sink takes."
            );
    }

    // The decoder writes what the sink takes when it can. A GPU sink that takes RGB (Spout, Syphon) gets
    // the decoder's own format on its storage and GPU instead, which a processor converts there; only
    // when no decoder reaches the sink's storage does decoding fall back to system memory.
    private IVideoDecoder CreateDecoder()
    {
        // An alpha layer is decoded into frames with alpha, in memory: no hardware decoder keeps it.
        if (_setup.Alpha == AlphaLayout.Layer)
        {
            return _registry.TryCreateVideoDecoder(
                _setup.Format,
                VideoConstraints.Cpu(PixelFormat.Yuva420),
                out IVideoDecoder? layered,
                AlphaLayout.Layer
            )
                ? layered
                : throw new InvalidOperationException(
                    $"No registered decoder decodes the alpha layer of {_setup.Format.Codec}."
                );
        }

        VideoConstraints sink = _sink.Constraints with { HeldFrames = HeldFrames };
        ImmutableArray<PixelFormat> any = [.. Enum.GetValues<PixelFormat>()];

        // A side-by-side frame is unpacked from the decoder's own 4:2:0: converted to the sink's RGB first,
        // its alpha half would be colour.
        VideoConstraints[] outputs =
            _setup.Alpha == AlphaLayout.PackSideBySide
                ?
                [
                    sink with
                    {
                        PixelFormats = [PixelFormat.Nv12, PixelFormat.I420],
                    },
                    VideoConstraints.Cpu(PixelFormat.Nv12, PixelFormat.I420) with
                    {
                        HeldFrames = HeldFrames,
                    },
                ]
                :
                [
                    sink,
                    sink with
                    {
                        PixelFormats = any,
                    },
                    VideoConstraints.Cpu(any) with
                    {
                        HeldFrames = HeldFrames,
                    },
                ];
        foreach (VideoConstraints output in outputs)
        {
            if (_registry.TryCreateVideoDecoder(_setup.Format, output, out IVideoDecoder? decoder))
            {
                return decoder;
            }
        }

        throw new InvalidOperationException(
            $"No registered decoder opened for {_setup.Format.Codec}."
        );
    }

    // Asks for a keyframe unless one was asked for within the interval; true when it asked.
    private void RequestKeyframe()
    {
        _setup.RequestKeyframe();
        LogKeyframeRequested();
    }

    [LoggerMessage(
        2072,
        LogLevel.Error,
        "The video decode worker stopped; no more video is played."
    )]
    private partial void LogWorkerFailed(Exception exception);

    [LoggerMessage(
        2071,
        LogLevel.Warning,
        "Video decoding fell {Frames} frames behind; they are dropped and decoding resumes at the next keyframe."
    )]
    private partial void LogBehind(int frames);

    [LoggerMessage(2050, LogLevel.Debug, "A video frame failed to decode; asking for a keyframe.")]
    private partial void LogDecodeFailed(Exception exception);

    [LoggerMessage(2051, LogLevel.Debug, "Asked the sender for a keyframe.")]
    private partial void LogKeyframeRequested();

    // Shows frames as playout releases them: as they are when the sink takes them, otherwise converted,
    // drawn straight into the surface the sink lends when it lends one, else through the processor's own
    // surfaces. A frame the sink skips, its surfaces all in use, is not converted. Playout releases from its scheduler or, for a frame without a capture time, from the
    // decode worker, so showing is serialised.
    private sealed class Presenter(VideoReceiveStream stream) : IVideoFrameConsumer, IDisposable
    {
        private readonly Lock _gate = new();
        private IVideoProcessor? _processor;
        private VideoStreamDescription? _described;
        private bool _disposed;

        public void OnFrame(in VideoFrame frame)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                VideoStreamDescription description = new(
                    frame.Storage.Kind,
                    frame.Format.PixelFormat,
                    new VideoSize(frame.Format.VisibleRect.Width, frame.Format.VisibleRect.Height),
                    frame.Storage.Device
                );
                if (description != _described)
                {
                    _processor?.Dispose();
                    _processor = stream.NeedsProcessing(description)
                        ? stream.CreateProcessor(description)
                        : null;
                    _described = description;
                }

                if (_processor is not { } processor)
                {
                    stream._sink.OnFrame(in frame);
                    return;
                }

                VideoStreamDescription output = processor.Info.Output;
                VideoFormat drawn = new(output.PixelFormat, output.Size.Width, output.Size.Height);
                VideoRenderResult result = stream._sink.Render(
                    drawn,
                    new Drawing(processor, frame),
                    static (in VideoTarget target, scoped in Drawing drawing) =>
                        drawing.Processor.TryProcess(in drawing.Frame, in target)
                );
                if (result == VideoRenderResult.Unavailable)
                {
                    processor.Process(in frame, stream._sink);
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _processor?.Dispose();
                _processor = null;
            }
        }
    }

    // A frame and the processor that draws it, for a sink that lends its surface.
    private readonly ref struct Drawing(IVideoProcessor processor, VideoFrame frame)
    {
        public readonly IVideoProcessor Processor = processor;

        public readonly VideoFrame Frame = frame;
    }
}
