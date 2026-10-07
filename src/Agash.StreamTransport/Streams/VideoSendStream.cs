using Agash.StreamTransport.Media;
using Agash.StreamTransport.Threading;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.Streams;

/// <summary>What a send stream sends: the negotiated format and how fast it starts.</summary>
/// <param name="Format">The negotiated codec format.</param>
/// <param name="StartBitsPerSecond">The rate to start at.</param>
/// <param name="Alpha">How the frames' transparency travels, as negotiated.</param>
internal sealed record VideoSendSetup(
    VideoCodecFormat Format,
    long StartBitsPerSecond,
    AlphaLayout Alpha = AlphaLayout.None
);

/// <summary>
/// Sends one video source to a peer. The source pushes frames; the stream keeps only the latest one not
/// yet encoded, so a slow encoder drops frames instead of building latency. A worker converts each frame
/// when the encoder cannot take it as it is (and packs alpha when asked), encodes it, and hands it to the
/// transport. The encoder is made when the first frame fixes the picture size, and remade when
/// the size changes. An encoder that fails before it has encoded a frame is passed over for the rest of
/// the stream and the next best one takes its place: a driver can refuse at the real size and format what
/// it accepted when probed.
/// </summary>
internal sealed partial class VideoSendStream : IVideoFrameConsumer, IAsyncDisposable
{
    private readonly VideoSendSetup _setup;
    private readonly MediaCodecRegistry _registry;
    private readonly MediaSessionOptions _options;
    private readonly EncodedVideoSink _send;
    private readonly FrameTimingTracker _timing = new();
    private readonly MediaClock _clock;
    private readonly StreamTransportMetrics _metrics;
    private readonly ILogger _logger;
    private readonly WakeSignal _wake;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly EncodedConsumer _encoded;
    private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
    private VideoConstraints _encoderInput;
    private readonly IDisposable _connection;
    private readonly Task _worker;
    private VideoFrameLease? _pending;
    private int _keyframeRequested = 1;
    private long _bitsPerSecond;
    private VideoStreamDescription? _described;
    private IVideoProcessor? _processor;
    private IVideoEncoder? _encoder;
    private VideoSize _encoderSize;
    private bool _encoderDelivered;
    private int _framesDropped;
    private int _framesSent;
    private readonly FrameRateMeter _rate;
    private MediaTime? _lastAccepted;

    /// <summary>Connects a source and starts the encode worker.</summary>
    /// <param name="source">The video source.</param>
    /// <param name="setup">The negotiated format and start rate.</param>
    /// <param name="registry">Where encoders and processors come from.</param>
    /// <param name="options">The session options.</param>
    /// <param name="send">Where encoded frames go.</param>
    /// <param name="clock">The media clock, for encode timing.</param>
    /// <param name="metrics">The library's instruments.</param>
    /// <param name="logger">The logger.</param>
    public VideoSendStream(
        IVideoSource source,
        VideoSendSetup setup,
        MediaCodecRegistry registry,
        MediaSessionOptions options,
        EncodedVideoSink send,
        MediaClock clock,
        StreamTransportMetrics metrics,
        ILogger logger
    )
    {
        _setup = setup;
        _registry = registry;
        _options = options;
        _send = send;
        _clock = clock;
        _metrics = metrics;
        _logger = logger;
        _metrics.VideoTargetBitrate.Record(
            setup.StartBitsPerSecond,
            StreamTransportMetrics.Codec(setup.Format.Codec)
        );
        _bitsPerSecond = setup.StartBitsPerSecond;
        _rate = new FrameRateMeter(
            Math.Min(
                (source as IVideoInput)?.Mode?.FrameRate ?? 30,
                options.MaxFrameRate ?? double.MaxValue
            )
        );
        _wake = new WakeSignal();
        _encoded = new EncodedConsumer(this);

        // The source is asked for what the encoder takes, so it can produce that directly; whatever it
        // cannot, a processor converts.
        _encoderInput = EncoderInput(
            registry.QueryVideoEncoder(setup.Format, device: null, alpha: setup.Alpha)
                ?? throw new InvalidOperationException(
                    $"No registered encoder encodes {setup.Format.Codec}"
                        + (setup.Alpha == AlphaLayout.Layer ? " with an alpha layer." : ".")
                )
        );
        _worker = Task.Run(() => RunAsync(_stop.Token));
        _connection = source.Connect(this, _encoderInput);
    }

    /// <summary>Frames the source delivered faster than the encoder took them, dropped unencoded.</summary>
    public int FramesDropped => Volatile.Read(ref _framesDropped);

    /// <summary>Encoded frames handed to the link.</summary>
    public int FramesSent => Volatile.Read(ref _framesSent);

    /// <summary>Makes the next frame a keyframe, as a receiver asked (PLI or FIR).</summary>
    public void RequestKeyframe() => Volatile.Write(ref _keyframeRequested, 1);

    /// <summary>Retunes the encoder to the congestion controller's target.</summary>
    /// <param name="bitsPerSecond">The target bit rate.</param>
    public void SetBitrate(long bitsPerSecond)
    {
        Interlocked.Exchange(ref _bitsPerSecond, bitsPerSecond);
        _wake.Signal();
    }

    /// <inheritdoc/>
    public void OnFrame(in VideoFrame frame)
    {
        MediaTime time = frame.Timestamp.Time;
        if (_options.MaxFrameRate is { } max && _lastAccepted is { } last)
        {
            // Thinning to the cap: a frame sooner than the cap allows, less a little for jitter, waits
            // its turn as the next frame.
            if (time - last < TimeSpan.FromSeconds(0.9 / max) && time > last)
            {
                return;
            }
        }

        _lastAccepted = time;
        lock (_gate)
        {
            _rate.Observe(time);
        }

        VideoFrameLease lease = frame.Retain();
        VideoFrameLease? replaced;
        lock (_gate)
        {
            replaced = _pending;
            _pending = lease;
        }

        if (replaced is not null)
        {
            // The encoder had not taken the previous frame before this one came.
            replaced.Dispose();
            Interlocked.Increment(ref _framesDropped);
            _metrics.VideoFramesDropped.Add(
                1,
                StreamTransportMetrics.Codec(_setup.Format.Codec),
                StreamTransportMetrics.Reason("encoder_busy")
            );
        }

        _wake.Signal();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _connection.Dispose();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _worker.ConfigureAwait(false);
        lock (_gate)
        {
            _pending?.Dispose();
            _pending = null;
        }

        _processor?.Dispose();
        EndEncoder();
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        long appliedRate = Interlocked.Read(ref _bitsPerSecond);
        double appliedFrames = FramesPerSecond;
        try
        {
            while (true)
            {
                await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
                long rate = Interlocked.Read(ref _bitsPerSecond);
                double frames = FramesPerSecond;
                bool framesMoved = FrameRateMeter.Moved(appliedFrames, frames);
                if ((rate != appliedRate || framesMoved) && _encoder is not null)
                {
                    if (framesMoved)
                    {
                        LogFrameRate(frames);
                    }

                    _encoder.Reconfigure(new RateTarget(rate, frames));
                    _metrics.VideoTargetBitrate.Record(
                        rate,
                        StreamTransportMetrics.Codec(_setup.Format.Codec)
                    );
                    appliedRate = rate;
                    appliedFrames = frames;
                }

                VideoFrameLease? lease;
                lock (_gate)
                {
                    lease = _pending;
                    _pending = null;
                }

                if (lease is null)
                {
                    continue;
                }

                using (lease)
                {
                    try
                    {
                        Send(lease.Frame);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        // A frame the pipeline cannot take is dropped; the pipeline is rebuilt for the
                        // next one, and a keyframe restarts the stream for the receiver.
                        LogFrameFailed(exception);
                        ResetPipeline();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Deliberately not logged: cancellation is how the stream stops.
        }
    }

    private void Send(in VideoFrame frame)
    {
        VideoStreamDescription description = new(
            frame.Storage.Kind,
            frame.Format.PixelFormat,
            new VideoSize(frame.Format.VisibleRect.Width, frame.Format.VisibleRect.Height),
            frame.Storage.Device
        );
        if (description != _described)
        {
            ResetPipeline();
            _processor = NeedsProcessing(description) ? CreateProcessor(description) : null;
            _described = description;
        }

        if (_processor is not null)
        {
            _processor.Process(in frame, _encoded);
        }
        else
        {
            Encode(in frame);
        }
    }

    // Called with each frame ready for the encoder, straight from the source or from the processor.
    private void Encode(in VideoFrame frame)
    {
        VideoSize size = new(frame.Format.VisibleRect.Width, frame.Format.VisibleRect.Height);
        if (_encoder is null || size != _encoderSize)
        {
            EndEncoder();
            _encoder = CreateEncoder(size, frame.Storage.Device);
            _encoderSize = size;
            _encoderDelivered = false;
            Volatile.Write(ref _keyframeRequested, 1);
        }

        bool keyframe = Interlocked.Exchange(ref _keyframeRequested, 0) == 1;
        try
        {
            MediaTime started = _clock.Now;
            _timing.EncodeStarted(frame.Timestamp.Origin, started);
            _encoder.Encode(in frame, new EncodeRequest(keyframe), _encoded);
            _metrics.VideoEncodeDuration.Record(
                (_clock.Now - started).TotalSeconds,
                StreamTransportMetrics.Codec(_setup.Format.Codec),
                StreamTransportMetrics.Implementation(_encoder.Info.ImplementationName)
            );
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException && !_encoderDelivered)
        {
            // Logged by the worker with the frame it cost; this only passes over the encoder.
            Refuse(_encoder.Info.ImplementationName);
            throw;
        }
    }

    // Passes over an encoder that never worked, and asks the next best what it takes.
    private void Refuse(string implementation)
    {
        _ = _refused.Add(implementation);
        _metrics.VideoEncoderFailures.Add(
            1,
            StreamTransportMetrics.Codec(_setup.Format.Codec),
            StreamTransportMetrics.Implementation(implementation)
        );
        if (
            _registry.QueryVideoEncoder(_setup.Format, device: null, _refused, _setup.Alpha) is
            { } next
        )
        {
            LogEncoderRefused(implementation, next.ImplementationName);
            _encoderInput = EncoderInput(next);
        }
        else
        {
            LogNoEncoderLeft(implementation, _setup.Format.Codec);
        }
    }

    // What the source is asked for: what the encoder takes, and for an alpha layer only the formats that
    // carry alpha, so the frames keep it on the way in.
    private VideoConstraints EncoderInput(VideoEncoderInfo encoder) =>
        _setup.Alpha == AlphaLayout.Layer
            ? encoder.Input with
            {
                PixelFormats =
                [
                    .. encoder.Input.PixelFormats.Where(static f =>
                        f is PixelFormat.Bgra or PixelFormat.Rgba or PixelFormat.Yuva420
                    ),
                ],
            }
            : encoder.Input;

    private bool NeedsProcessing(VideoStreamDescription description) =>
        _setup.Alpha == AlphaLayout.PackSideBySide
        || !_encoderInput.Storages.Contains(description.Storage)
        || !_encoderInput.PixelFormats.Contains(description.PixelFormat);

    private IVideoProcessor CreateProcessor(VideoStreamDescription description) =>
        _registry.TryCreateVideoProcessor(
            description,
            new VideoProcessing(
                _encoderInput,
                Alpha: _setup.Alpha == AlphaLayout.PackSideBySide
                    ? AlphaLayout.PackSideBySide
                    : AlphaLayout.None
            ),
            out IVideoProcessor? processor
        )
            ? processor
            : throw new InvalidOperationException(
                $"No registered processor turns {description} into what the {_setup.Format.Codec} encoder takes."
            );

    private IVideoEncoder CreateEncoder(VideoSize size, GpuIdentity? device) =>
        _registry.TryCreateVideoEncoder(
            new VideoEncoderConfiguration(
                _setup.Format,
                size,
                new RateTarget(Interlocked.Read(ref _bitsPerSecond), FramesPerSecond),
                _options.KeyframeInterval,
                _options.VideoTuning,
                Alpha: _setup.Alpha == AlphaLayout.Layer ? AlphaLayout.Layer : AlphaLayout.None
            ),
            device,
            out IVideoEncoder? encoder,
            _refused
        )
            ? encoder
            : throw new InvalidOperationException(
                $"No registered encoder opened for {_setup.Format.Codec} at {size}."
            );

    private void ResetPipeline()
    {
        _processor?.Dispose();
        _processor = null;
        EndEncoder();
        _described = null;
        Volatile.Write(ref _keyframeRequested, 1);
    }

    // Ends the encoder's stream before closing it; what it still held is not sent, since the stream
    // it belonged to is ending.
    private void EndEncoder()
    {
        if (_encoder is { } encoder)
        {
            _encoder = null;
            try
            {
                encoder.Flush(Discarded.Instance);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // An encoder that failed mid-stream may refuse to end it; it is closed all the same.
                LogFlushFailed(exception);
            }

            encoder.Dispose();
        }
    }

    // The source's rate under the cap, as rate control plans for it.
    private double FramesPerSecond
    {
        get
        {
            lock (_gate)
            {
                return Math.Min(_rate.FramesPerSecond, _options.MaxFrameRate ?? double.MaxValue);
            }
        }
    }

    [LoggerMessage(
        2044,
        LogLevel.Information,
        "The source sends {FramesPerSecond:0.#} frames a second; rate control plans for it."
    )]
    private partial void LogFrameRate(double framesPerSecond);

    [LoggerMessage(
        2040,
        LogLevel.Warning,
        "A video frame could not be sent; the pipeline restarts with the next one."
    )]
    private partial void LogFrameFailed(Exception exception);

    [LoggerMessage(2041, LogLevel.Debug, "Ending the encoder's stream failed; it is closed.")]
    private partial void LogFlushFailed(Exception exception);

    [LoggerMessage(
        2042,
        LogLevel.Warning,
        "{Encoder} failed before encoding a frame; {Next} takes its place."
    )]
    private partial void LogEncoderRefused(string encoder, string next);

    [LoggerMessage(
        2043,
        LogLevel.Error,
        "{Encoder} failed before encoding a frame, and no other encoder encodes {Codec}."
    )]
    private partial void LogNoEncoderLeft(string encoder, VideoCodecId codec);

    // An encoded frame to the transport.
    private void Transmit(in EncodedVideoFrame frame)
    {
        _encoderDelivered = true;
        FrameSendTiming? timing = _timing.EncodeFinished(
            frame.Timestamp.Origin,
            frame.Data.Length,
            Interlocked.Read(ref _bitsPerSecond),
            FramesPerSecond,
            _clock.Now
        );
        _send(in frame, timing);
        Interlocked.Increment(ref _framesSent);
        _metrics.VideoFramesSent.Add(1, StreamTransportMetrics.Codec(_setup.Format.Codec));
    }

    private sealed class Discarded : IEncodedVideoConsumer
    {
        public static Discarded Instance { get; } = new();

        public void OnEncoded(in EncodedVideoFrame frame) { }
    }

    // Takes processed frames into the encoder and encoded frames to the transport.
    private sealed class EncodedConsumer(VideoSendStream stream)
        : IVideoFrameConsumer,
            IEncodedVideoConsumer
    {
        public void OnFrame(in VideoFrame frame) => stream.Encode(in frame);

        public void OnEncoded(in EncodedVideoFrame frame) => stream.Transmit(in frame);
    }
}
