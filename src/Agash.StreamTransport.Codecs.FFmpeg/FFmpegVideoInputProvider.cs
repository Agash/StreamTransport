using System.Collections.Immutable;
using System.Globalization;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Codecs.FFmpeg;

/// <summary>
/// Cameras through FFmpeg's capture devices (<c>dshow</c> on Windows, <c>v4l2</c> on Linux,
/// <c>avfoundation</c> on macOS): the fallback for a device the platform's own provider cannot open, and
/// the way to cameras that send only MJPEG, which it decodes. It ranks below the native providers.
/// </summary>
/// <param name="loggerFactory">Where inputs log.</param>
public sealed partial class FFmpegVideoInputProvider(ILoggerFactory? loggerFactory = null) : IVideoInputProvider
{
    private readonly ILoggerFactory _loggers = loggerFactory ?? NullLoggerFactory.Instance;

    /// <inheritdoc/>
    public string Name => "ffmpeg";

    /// <inheritdoc/>
    public int Rank => 10;

    // The platform's capture format.
    internal static string DeviceFormat =>
        OperatingSystem.IsWindows() ? "dshow"
        : OperatingSystem.IsMacOS() ? "avfoundation"
        : "v4l2";

    /// <inheritdoc/>
    public ValueTask<ImmutableArray<VideoInputInfo>> GetInputsAsync(CancellationToken cancellationToken)
    {
        ImmutableArray<FF.CaptureDevice> devices = FF.MediaReader.ListDevices(DeviceFormat);
        return ValueTask.FromResult<ImmutableArray<VideoInputInfo>>(
            [
                .. devices
                    .Where(static d => d.MediaTypes.IsEmpty || d.MediaTypes.Contains(FF.MediaType.Video))
                    .Select(d => new VideoInputInfo(
                        Name,
                        d.Name,
                        d.Description.Length > 0 ? d.Description : d.Name,
                        MediaInputKind.Camera,
                        []
                    )
                    {
                        // A V4L2 node is the same device the V4L2 provider lists.
                        DeviceKey = d.Name.StartsWith("/dev/", StringComparison.Ordinal) ? d.Name : null,
                    }),
            ]
        );
    }

    /// <inheritdoc/>
    public ValueTask<IVideoInput> OpenAsync(
        VideoInputInfo input,
        VideoInputRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(request);
        Dictionary<string, string> options = [];
        if (request.Size is { } size)
        {
            options["video_size"] = FormattableString.Invariant($"{size.Width}x{size.Height}");
        }

        if (request.FrameRate is { } rate)
        {
            options["framerate"] = rate.ToString(CultureInfo.InvariantCulture);
        }

        string device = DeviceFormat == "dshow" ? $"video={input.Id}" : input.Id;
        FF.MediaReader reader;
        try
        {
            reader = FF.MediaReader.OpenDevice(DeviceFormat, device, options);
        }
        catch (FF.FFmpegException exception)
        {
            throw new IOException($"FFmpeg could not open {input.Name}.", exception);
        }

        return ValueTask.FromResult<IVideoInput>(new FFmpegVideoInput(input, reader, _loggers));
    }
}

// Reads and decodes packets on a thread of its own while a consumer is connected.
internal sealed partial class FFmpegVideoInput : IVideoInput, IVideoFrameRetainer
{
    private static readonly TimeSpan PlausibleSkew = TimeSpan.FromSeconds(2);

    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly FF.MediaReader _reader;
    private readonly FF.MediaStream _stream;
    private readonly FF.Decoder _decoder;
    private readonly FF.Frame _decoded = new();
    private readonly FF.Frame _converted = new();
    private readonly FF.Packet _packet = new();
    private FF.Scaler? _scaler;
    private ImmutableArray<IVideoFrameConsumer> _consumers = [];
    private Thread? _thread;
    private volatile bool _stopping;
    private bool _disposed;

    public FFmpegVideoInput(VideoInputInfo info, FF.MediaReader reader, ILoggerFactory loggers)
    {
        Info = info;
        _reader = reader;
        _logger = loggers.CreateLogger<FFmpegVideoInput>();
        _stream =
            reader.FindBestStream(FF.MediaType.Video)
            ?? throw new InvalidOperationException($"{info.Name} has no video stream.");
        _decoder = _stream.CreateDecoder(options: new FF.DecoderOptions { LowDelay = true, PacketTimeBase = _stream.TimeBase });
        FF.Rational rate = _stream.FrameRate;
        Mode = new VideoInputMode(
            PixelFormat.Nv12,
            new VideoSize(_stream.Width, _stream.Height),
            rate.Denominator == 0 ? 30 : Math.Round((double)rate.Numerator / rate.Denominator, 3)
        );
        LogOpened(info.Name, _stream.CodecId.ToString(), Mode.Value);
    }

    public VideoInputInfo Info { get; }

    public VideoInputMode? Mode { get; }

    public IDisposable Connect(IVideoFrameConsumer consumer, VideoConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _consumers = _consumers.Add(consumer);
            if (_thread is null)
            {
                _stopping = false;
                _thread = new Thread(Run) { IsBackground = true, Name = $"FFmpeg capture {Info.Name}" };
                _thread.Start();
            }
        }

        return new Connection(this, consumer);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _consumers = [];
        }

        Stop();
        _decoder.Dispose();
        _reader.Dispose();
        _decoded.Dispose();
        _converted.Dispose();
        _packet.Dispose();
        _scaler?.Dispose();
    }

    VideoFrameLease IVideoFrameRetainer.Retain(in VideoFrame frame) => CopyOf(in frame);

    private static VideoFrameLease CopyOf(in VideoFrame frame)
    {
        // A copy into pooled memory; the decoder reuses its frame for the next picture.
        VideoFrame plain = new(
            frame.Format,
            frame.Timestamp,
            frame.GetPlane(0),
            ((CpuImage)frame.Storage.Value!).Planes[0].Stride,
            frame.PlaneCount > 1 ? frame.GetPlane(1) : default,
            frame.PlaneCount > 1 ? ((CpuImage)frame.Storage.Value!).Planes[1].Stride : 0,
            frame.PlaneCount > 2 ? frame.GetPlane(2) : default,
            frame.PlaneCount > 2 ? ((CpuImage)frame.Storage.Value!).Planes[2].Stride : 0,
            color: frame.Color
        );
        return plain.Retain();
    }

    private void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _thread = null;
            _stopping = true;
        }

        thread?.Join();
    }

    private void Run()
    {
        try
        {
            while (!_stopping && _reader.TryReadPacket(_packet))
            {
                if (_packet.StreamIndex != _stream.Index)
                {
                    continue;
                }

                MediaTimestamp timestamp = Timestamp(_packet.PresentationTimestamp);
                foreach (FF.Frame frame in _decoder.Decode(_packet, _decoded))
                {
                    Deliver(frame, timestamp);
                }
            }
        }
        catch (FF.FFmpegException exception)
        {
            LogReadFailed(exception, Info.Name);
        }
    }

    private void Deliver(FF.Frame decoded, MediaTimestamp timestamp)
    {
        FF.Frame source = decoded;
        PixelFormat? format = Formats.FromFFmpeg(decoded.PixelFormat);
        if (format is null)
        {
            // MJPEG decodes to full-range planar 4:2:x, which the model does not carry: made NV12.
            _scaler ??= new FF.Scaler();
            _converted.Reset();
            _converted.Width = decoded.Width;
            _converted.Height = decoded.Height;
            _converted.PixelFormat = FF.PixelFormat.Nv12;
            _scaler.Scale(decoded, _converted);
            _converted.CopyPropertiesFrom(decoded);
            source = _converted;
            format = PixelFormat.Nv12;
        }

        VideoColor color = new(
            ColorMatrix.Bt601,
            source.ColorRange == FF.ColorRange.Full ? ColorRange.Full : ColorRange.Limited,
            ColorPrimaries.Bt709,
            TransferFunction.Bt709
        );
        VideoFrame frame = FFmpegFrames.View(
            source,
            new VideoFormat(format.Value, source.Width, source.Height),
            timestamp,
            color,
            this
        );
        foreach (IVideoFrameConsumer consumer in _consumers)
        {
            try
            {
                consumer.OnFrame(in frame);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // One consumer's failure does not stop the others or the capture.
                LogConsumerFailed(exception);
            }
        }
    }

    // A packet time on the media clock (v4l2's monotonic stamps) is the capture time; others are not.
    private MediaTimestamp Timestamp(long? pts)
    {
        MediaTime now = MediaClock.System.Now;
        if (pts is not { } value)
        {
            return MediaTimestamp.Observed(now);
        }

        FF.Rational timeBase = _stream.TimeBase;
        var captured = new MediaTime((long)((Int128)value * timeBase.Numerator * 1_000_000_000 / timeBase.Denominator));
        return (now - captured).Duration() < PlausibleSkew
            ? MediaTimestamp.Captured(captured)
            : MediaTimestamp.Observed(now);
    }

    private void Disconnect(IVideoFrameConsumer consumer)
    {
        bool last;
        lock (_gate)
        {
            _consumers = _consumers.Remove(consumer);
            last = _consumers.IsEmpty;
        }

        if (last)
        {
            Stop();
        }
    }

    [LoggerMessage(1030, LogLevel.Information, "Opened {Camera} through FFmpeg: {Codec}, {Mode}.")]
    private partial void LogOpened(string camera, string codec, VideoInputMode mode);

    [LoggerMessage(1031, LogLevel.Error, "Reading {Camera} through FFmpeg failed; its capture stops.")]
    private partial void LogReadFailed(Exception exception, string camera);

    [LoggerMessage(1032, LogLevel.Warning, "A consumer failed to take a captured frame.")]
    private partial void LogConsumerFailed(Exception exception);

    private sealed class Connection(FFmpegVideoInput input, IVideoFrameConsumer consumer) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                input.Disconnect(consumer);
            }
        }
    }
}
