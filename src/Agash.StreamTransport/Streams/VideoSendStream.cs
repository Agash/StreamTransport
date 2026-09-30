using Agash.StreamTransport.Media;
using Agash.StreamTransport.Rtp;
using Agash.StreamTransport.Threading;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.Streams;

/// <summary>What a video send stream is built from.</summary>
/// <param name="Format">The negotiated codec and its parameters.</param>
/// <param name="Writer">Turns encoded frames into RTP.</param>
/// <param name="StartBitsPerSecond">The rate to start encoding at.</param>
internal sealed record VideoSendSetup(
    VideoCodecFormat Format,
    RtpStreamWriter Writer,
    long StartBitsPerSecond
);

/// <summary>
/// Sends one video source to a peer. The source pushes frames; the stream keeps only the latest one not
/// yet encoded, so a slow encoder drops frames instead of building latency. A worker converts each frame
/// when the encoder cannot take it as it is (and packs alpha when asked), encodes it, and queues its RTP
/// packets on the pacer. The encoder is made when the first frame fixes the picture size, and remade when
/// the size changes.
/// </summary>
internal sealed partial class VideoSendStream : IVideoFrameConsumer, IAsyncDisposable
{
    private readonly VideoSendSetup _setup;
    private readonly MediaCodecRegistry _registry;
    private readonly MediaSessionOptions _options;
    private readonly RtpPacer _pacer;
    private readonly ILogger _logger;
    private readonly WakeSignal _wake;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly EncodedConsumer _encoded;
    private readonly VideoConstraints _encoderInput;
    private readonly IDisposable _connection;
    private readonly Task _worker;
    private VideoFrameLease? _pending;
    private int _keyframeRequested = 1;
    private long _bitsPerSecond;
    private VideoStreamDescription? _described;
    private IVideoProcessor? _processor;
    private IVideoEncoder? _encoder;
    private VideoSize _encoderSize;
    private int _framesDropped;

    /// <summary>Connects a source and starts the encode worker.</summary>
    /// <param name="source">The video source.</param>
    /// <param name="setup">The negotiated format and RTP writer.</param>
    /// <param name="registry">Where encoders and processors come from.</param>
    /// <param name="options">The session options.</param>
    /// <param name="pacer">Where packets go.</param>
    /// <param name="timeProvider">The clock the worker waits on.</param>
    /// <param name="logger">The logger.</param>
    public VideoSendStream(
        IVideoSource source,
        VideoSendSetup setup,
        MediaCodecRegistry registry,
        MediaSessionOptions options,
        RtpPacer pacer,
        TimeProvider timeProvider,
        ILogger logger
    )
    {
        _setup = setup;
        _registry = registry;
        _options = options;
        _pacer = pacer;
        _logger = logger;
        _bitsPerSecond = setup.StartBitsPerSecond;
        _wake = new WakeSignal(timeProvider);
        _encoded = new EncodedConsumer(this);

        // The source is asked for what the encoder takes, so it can produce that directly; whatever it
        // cannot, a processor converts.
        _encoderInput =
            registry.QueryVideoEncoder(setup.Format, device: null)?.Input
            ?? throw new InvalidOperationException(
                $"No registered encoder encodes {setup.Format.Codec}."
            );
        _worker = Task.Run(() => RunAsync(_stop.Token));
        _connection = source.Connect(this, _encoderInput);
    }

    /// <summary>Frames the source delivered faster than the encoder took them, dropped unencoded.</summary>
    public int FramesDropped => Volatile.Read(ref _framesDropped);

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
        VideoFrameLease lease = frame.Retain();
        VideoFrameLease? replaced;
        lock (_gate)
        {
            replaced = _pending;
            _pending = lease;
        }

        if (replaced is not null)
        {
            replaced.Dispose();
            Interlocked.Increment(ref _framesDropped);
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
        _encoder?.Dispose();
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        long appliedRate = Interlocked.Read(ref _bitsPerSecond);
        try
        {
            while (true)
            {
                await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
                long rate = Interlocked.Read(ref _bitsPerSecond);
                if (rate != appliedRate && _encoder is not null)
                {
                    _encoder.Reconfigure(new RateTarget(rate, _options.FrameRate));
                    appliedRate = rate;
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
            _encoder?.Dispose();
            _encoder = CreateEncoder(size, frame.Storage.Device);
            _encoderSize = size;
            Volatile.Write(ref _keyframeRequested, 1);
        }

        bool keyframe = Interlocked.Exchange(ref _keyframeRequested, 0) == 1;
        _encoder.Encode(in frame, new EncodeRequest(keyframe), _encoded);
    }

    private bool NeedsProcessing(VideoStreamDescription description) =>
        _options.Alpha == AlphaLayout.PackSideBySide
        || !_encoderInput.Storages.Contains(description.Storage)
        || !_encoderInput.PixelFormats.Contains(description.PixelFormat);

    private IVideoProcessor CreateProcessor(VideoStreamDescription description) =>
        _registry.TryCreateVideoProcessor(
            description,
            new VideoProcessing(_encoderInput, Alpha: _options.Alpha),
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
                new RateTarget(Interlocked.Read(ref _bitsPerSecond), _options.FrameRate),
                _options.KeyframeInterval,
                _options.VideoTuning
            ),
            device,
            out IVideoEncoder? encoder
        )
            ? encoder
            : throw new InvalidOperationException(
                $"No registered encoder opened for {_setup.Format.Codec} at {size}."
            );

    private void ResetPipeline()
    {
        _processor?.Dispose();
        _processor = null;
        _encoder?.Dispose();
        _encoder = null;
        _described = null;
        Volatile.Write(ref _keyframeRequested, 1);
    }

    [LoggerMessage(
        2040,
        LogLevel.Warning,
        "A video frame could not be sent; the pipeline restarts with the next one."
    )]
    private partial void LogFrameFailed(Exception exception);

    // Takes processed frames into the encoder and encoded frames into RTP.
    private sealed class EncodedConsumer(VideoSendStream stream)
        : IVideoFrameConsumer,
            IEncodedVideoConsumer
    {
        public void OnFrame(in VideoFrame frame) => stream.Encode(in frame);

        public void OnEncoded(in EncodedVideoFrame frame) =>
            stream._setup.Writer.Write(frame.Data, frame.Timestamp, stream._pacer.EnqueueVideo);
    }
}
