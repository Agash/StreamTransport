using System.Collections.Immutable;
using System.Threading.Channels;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.Sync;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.Streams;

/// <summary>What a video receive stream is built from.</summary>
/// <param name="Format">The negotiated codec and its parameters.</param>
/// <param name="PayloadFormat">The RTP payload format of the codec.</param>
/// <param name="Alpha">How the sender laid out transparency.</param>
/// <param name="RequestKeyframe">Asks the sender for a keyframe (PLI).</param>
internal sealed record VideoReceiveSetup(
    VideoCodecFormat Format,
    RtpPayloadFormat PayloadFormat,
    AlphaLayout Alpha,
    Func<ValueTask> RequestKeyframe
);

/// <summary>
/// Receives one video stream from a peer. RTP goes into a frame buffer that reorders it and hands over
/// only complete frames, asking the sender for a keyframe when a frame cannot be completed. A worker
/// decodes, converts what the sink cannot take (and unpacks alpha), and plays each frame on arrival or
/// at its synced slot.
/// </summary>
internal sealed partial class VideoReceiveStream : IVideoFrameConsumer, IAsyncDisposable
{
    // At most one keyframe request per interval, so a burst of loss is not a burst of requests.
    private static readonly TimeSpan KeyframeRequestInterval = TimeSpan.FromMilliseconds(250);

    // How long a sequence gap may wait for NACK and RTX to fill it before a keyframe is requested.
    private static readonly TimeSpan GapPatience = TimeSpan.FromMilliseconds(100);

    private readonly VideoReceiveSetup _setup;
    private readonly IVideoSink _sink;
    private readonly MediaCodecRegistry _registry;
    private readonly Playout _playout;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly RtpFrameBuffer _buffer;
    private readonly RtpClockAligner _aligner = new(new ClockRate(90_000));
    private readonly ArrivalStamps _stamps;
    private readonly Channel<(
        EncodedFrameBuffer Frame,
        bool Keyframe,
        MediaTimestamp Stamp
    )> _frames = Channel.CreateUnbounded<(EncodedFrameBuffer, bool, MediaTimestamp)>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );
    private readonly SinkConsumer _processed;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private IVideoDecoder? _decoder;
    private IVideoProcessor? _processor;
    private VideoStreamDescription? _described;
    private long _lastKeyframeRequest;
    private long? _gapSince;
    private int _framesDecoded;
    private int _framesFailed;

    /// <summary>Starts the decode worker.</summary>
    /// <param name="setup">The negotiated format and keyframe request.</param>
    /// <param name="sink">Where frames go.</param>
    /// <param name="registry">Where the decoder and processors come from.</param>
    /// <param name="playout">The session's playout.</param>
    /// <param name="clock">The local media clock.</param>
    /// <param name="logger">The logger.</param>
    public VideoReceiveStream(
        VideoReceiveSetup setup,
        IVideoSink sink,
        MediaCodecRegistry registry,
        Playout playout,
        MediaClock clock,
        ILogger logger
    )
    {
        _setup = setup;
        _sink = sink;
        _registry = registry;
        _playout = playout;
        _time = clock.TimeProvider;
        _logger = logger;
        _buffer = new RtpFrameBuffer(setup.PayloadFormat);
        _stamps = new ArrivalStamps(clock);
        _processed = new SinkConsumer(this);
        _lastKeyframeRequest =
            _time.GetTimestamp()
            - (long)(KeyframeRequestInterval.TotalSeconds * _time.TimestampFrequency);
        _worker = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>Frames decoded.</summary>
    public int FramesDecoded => Volatile.Read(ref _framesDecoded);

    /// <summary>Frames the decoder rejected, usually for a loss inside them that was not repaired.</summary>
    public int FramesFailed => Volatile.Read(ref _framesFailed);

    /// <summary>Takes one RTP packet of the stream, on the transport's receive thread.</summary>
    /// <param name="header">The packet's header.</param>
    /// <param name="payload">Its payload, borrowed for the call.</param>
    public void OnPacket(in RtpHeader header, ReadOnlySpan<byte> payload)
    {
        if (header.AbsoluteCaptureTimeNtp is { } ntp and not 0)
        {
            _aligner.Record(new NtpTime(ntp), header.Timestamp);
        }

        RtpFrameBuffer.InsertResult result = _buffer.Insert(
            header.SequenceNumber,
            header.Timestamp,
            header.Marker,
            payload
        );
        foreach (RtpFrameBuffer.AssembledFrame frame in result.Frames)
        {
            NtpTime? capture = _aligner.TryGetCapture(frame.Timestamp, out NtpTime at) ? at : null;
            if (!_frames.Writer.TryWrite((frame.Frame, frame.IsKeyframe, _stamps.Stamp(capture))))
            {
                frame.Frame.Dispose();
            }
        }

        // Gap tracking lives on this thread; the decode worker shares only the throttle.
        long now = _time.GetTimestamp();
        bool required = result.KeyframeRequired;
        if (_buffer.HasUnresolvedGap)
        {
            _gapSince ??= now;
            required |= _time.GetElapsedTime(_gapSince.Value, now) >= GapPatience;
        }
        else
        {
            _gapSince = null;
        }

        if (required && TryRequestKeyframe())
        {
            _gapSince = null;
        }
    }

    /// <inheritdoc/>
    public void OnFrame(in VideoFrame frame)
    {
        VideoStreamDescription description = new(
            frame.Storage.Kind,
            frame.Format.PixelFormat,
            new VideoSize(frame.Format.VisibleRect.Width, frame.Format.VisibleRect.Height),
            frame.Storage.Device
        );
        if (description != _described)
        {
            _processor?.Dispose();
            _processor = NeedsProcessing(description) ? CreateProcessor(description) : null;
            _described = description;
        }

        if (_processor is not null)
        {
            _processor.Process(in frame, _processed);
        }
        else
        {
            Play(in frame);
        }
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

        _processor?.Dispose();
        _decoder?.Dispose();
        _buffer.Dispose();
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
                using (frame)
                {
                    Decode(frame, keyframe, stamp);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Deliberately not logged: cancellation is how the stream stops.
        }
    }

    private void Decode(EncodedFrameBuffer frame, bool keyframe, MediaTimestamp stamp)
    {
        try
        {
            _decoder ??= CreateDecoder();
            _decoder.Decode(
                new EncodedVideoFrame(frame.Span, _setup.Format.Codec, keyframe, stamp),
                this
            );
            Interlocked.Increment(ref _framesDecoded);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A frame the decoder rejects loses the picture until the next keyframe, so one is asked for.
            Interlocked.Increment(ref _framesFailed);
            LogDecodeFailed(exception);
            _ = _stamps.TakeCapture(stamp);
            _ = TryRequestKeyframe();
        }
    }

    private void Play(in VideoFrame frame) =>
        _playout.Video(in frame, _stamps.TakeCapture(frame.Timestamp), _sink);

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

        VideoConstraints sink = _sink.Constraints;
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
                    VideoConstraints.Cpu(PixelFormat.Nv12, PixelFormat.I420),
                ]
                : [sink, sink with { PixelFormats = any }, VideoConstraints.Cpu(any)];
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
    private bool TryRequestKeyframe()
    {
        long now = _time.GetTimestamp();
        long last = Interlocked.Read(ref _lastKeyframeRequest);
        if (
            _time.GetElapsedTime(last, now) < KeyframeRequestInterval
            || Interlocked.CompareExchange(ref _lastKeyframeRequest, now, last) != last
        )
        {
            return false;
        }

        _ = RequestKeyframeAsync();
        return true;
    }

    private async Task RequestKeyframeAsync()
    {
        try
        {
            await _setup.RequestKeyframe().ConfigureAwait(false);
            LogKeyframeRequested();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The request is repeated at the next loss that needs it.
            LogKeyframeRequestFailed(exception);
        }
    }

    [LoggerMessage(2050, LogLevel.Debug, "A video frame failed to decode; asking for a keyframe.")]
    private partial void LogDecodeFailed(Exception exception);

    [LoggerMessage(2051, LogLevel.Debug, "Asked the sender for a keyframe.")]
    private partial void LogKeyframeRequested();

    [LoggerMessage(2052, LogLevel.Warning, "Asking the sender for a keyframe failed.")]
    private partial void LogKeyframeRequestFailed(Exception exception);

    // Takes processed frames to playout.
    private sealed class SinkConsumer(VideoReceiveStream stream) : IVideoFrameConsumer
    {
        public void OnFrame(in VideoFrame frame) => stream.Play(in frame);
    }
}
