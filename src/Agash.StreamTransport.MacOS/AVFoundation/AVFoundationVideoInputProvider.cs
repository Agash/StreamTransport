using System.Collections.Immutable;
using AVFoundation;
using Agash.StreamTransport.MacOS.Metal;
using Agash.StreamTransport.Media;
using CoreFoundation;
using CoreMedia;
using CoreVideo;
using Foundation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.MacOS.AVFoundation;

/// <summary>
/// Cameras through AVFoundation: built-in and external cameras, USB capture cards, Continuity Camera and
/// virtual cameras (camera extensions). Each frame is the camera's own IOSurface-backed buffer, lent to a
/// consumer on the GPU as an IOSurface and to others as its mapped memory; timestamps are the capture
/// times AVFoundation reports on the host clock, which is the media clock on macOS.
/// </summary>
/// <param name="loggerFactory">Where inputs log.</param>
public sealed partial class AVFoundationVideoInputProvider(ILoggerFactory? loggerFactory = null)
    : IVideoInputProvider
{
    private readonly ILoggerFactory _loggers = loggerFactory ?? NullLoggerFactory.Instance;

    /// <inheritdoc/>
    public string Name => "avfoundation";

    /// <inheritdoc/>
    public int Rank => 100;

    /// <inheritdoc/>
    public ValueTask<ImmutableArray<VideoInputInfo>> GetInputsAsync(CancellationToken cancellationToken)
    {
        using var discovery = AVCaptureDeviceDiscoverySession.Create(
            [
                AVCaptureDeviceType.BuiltInWideAngleCamera,
                AVCaptureDeviceType.External,
                AVCaptureDeviceType.ContinuityCamera,
            ],
            AVMediaTypes.Video,
            AVCaptureDevicePosition.Unspecified
        );
        return ValueTask.FromResult<ImmutableArray<VideoInputInfo>>(
            [
                .. discovery
                    .Devices.Select(d => new VideoInputInfo(Name, d.UniqueID, d.LocalizedName, MediaInputKind.Camera, AVFoundationFormats.Modes(d))
                    {
                        DeviceKey = d.UniqueID,
                    })
                    .Where(static i => !i.Modes.IsEmpty),
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
        VideoInputMode mode =
            request.Choose(input.Modes)
            ?? throw new NotSupportedException($"{input.Name} has no mode this provider delivers.");
        AVCaptureDevice device =
            AVCaptureDevice.DeviceWithUniqueID(input.Id)
            ?? throw new InvalidOperationException($"{input.Name} is gone.");
        return ValueTask.FromResult<IVideoInput>(new AVFoundationVideoInput(input, mode, device, _loggers));
    }
}

// The camera formats the media model has, by Core Video pixel format.
internal static class AVFoundationFormats
{
    public static PixelFormat? ToMedia(CVPixelFormatType format) =>
        format switch
        {
            CVPixelFormatType.CV420YpCbCr8BiPlanarVideoRange
            or CVPixelFormatType.CV420YpCbCr8BiPlanarFullRange => PixelFormat.Nv12,
            CVPixelFormatType.CV422YpCbCr8_yuvs => PixelFormat.Yuy2,
            CVPixelFormatType.CV422YpCbCr8 => PixelFormat.Uyvy,
            CVPixelFormatType.CV32BGRA => PixelFormat.Bgra,
            _ => null,
        };

    public static ImmutableArray<VideoInputMode> Modes(AVCaptureDevice device)
    {
        ImmutableArray<VideoInputMode>.Builder modes = ImmutableArray.CreateBuilder<VideoInputMode>();
        foreach (AVCaptureDeviceFormat format in device.Formats)
        {
            if (
                format.FormatDescription is not CMVideoFormatDescription description
                || ToMedia((CVPixelFormatType)description.MediaSubType) is not { } pixels
            )
            {
                continue;
            }

            CMVideoDimensions size = description.Dimensions;
            foreach (AVFrameRateRange range in format.VideoSupportedFrameRateRanges)
            {
                VideoInputMode mode = new(pixels, new VideoSize(size.Width, size.Height), Math.Round(range.MaxFrameRate, 3));
                if (!modes.Contains(mode))
                {
                    modes.Add(mode);
                }
            }
        }

        return modes.ToImmutable();
    }
}

// An open camera: a capture session whose video output calls back on a queue of its own.
internal sealed partial class AVFoundationVideoInput : IVideoInput
{
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly AVCaptureSession _session = new();
    private readonly AVCaptureVideoDataOutput _output = new();
    private readonly DispatchQueue _queue;
    private readonly Delegate _delegate;
    private ImmutableArray<(IVideoFrameConsumer Consumer, VideoConstraints Constraints)> _consumers = [];
    private bool _disposed;

    public AVFoundationVideoInput(
        VideoInputInfo info,
        VideoInputMode mode,
        AVCaptureDevice device,
        ILoggerFactory loggers
    )
    {
        Info = info;
        Mode = mode;
        _logger = loggers.CreateLogger<AVFoundationVideoInput>();
        _queue = new DispatchQueue($"StreamTransport camera {info.Name}");
        _delegate = new Delegate(this);

        AVCaptureDeviceFormat format =
            device.Formats.FirstOrDefault(f =>
                f.FormatDescription is CMVideoFormatDescription d
                && AVFoundationFormats.ToMedia((CVPixelFormatType)d.MediaSubType) == mode.PixelFormat
                && d.Dimensions.Width == mode.Size.Width
                && d.Dimensions.Height == mode.Size.Height
                && f.VideoSupportedFrameRateRanges.Any(r => Math.Abs(r.MaxFrameRate - mode.FrameRate) < 0.01 || (r.MinFrameRate <= mode.FrameRate && r.MaxFrameRate >= mode.FrameRate))
            ) ?? throw new NotSupportedException($"{info.Name} no longer offers {mode}.");
        var pixels = (CVPixelFormatType)((CMVideoFormatDescription)format.FormatDescription!).MediaSubType;

        AVCaptureDeviceInput input =
            AVCaptureDeviceInput.FromDevice(device, out NSError? error)
            ?? throw new InvalidOperationException($"{info.Name} could not be opened: {error?.LocalizedDescription}");
        _session.BeginConfiguration();
        if (!_session.CanAddInput(input))
        {
            throw new InvalidOperationException($"{info.Name} cannot be captured now.");
        }

        _session.AddInput(input);
        _output.AlwaysDiscardsLateVideoFrames = true;
        _output.WeakVideoSettings = new CVPixelBufferAttributes { PixelFormatType = pixels }.Dictionary;
        _output.SetSampleBufferDelegate(_delegate, _queue);
        _session.AddOutput(_output);
        if (device.LockForConfiguration(out error))
        {
            device.ActiveFormat = format;
            var frame = CMTime.FromSeconds(1 / mode.FrameRate, 1_000_000);
            device.ActiveVideoMinFrameDuration = frame;
            device.ActiveVideoMaxFrameDuration = frame;
            device.UnlockForConfiguration();
        }

        _session.CommitConfiguration();
        LogOpened(info.Name, mode);
    }

    public VideoInputInfo Info { get; }

    public VideoInputMode? Mode { get; }

    public IDisposable Connect(IVideoFrameConsumer consumer, VideoConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(constraints);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _consumers = _consumers.Add((consumer, constraints));
            if (!_session.Running)
            {
                _session.StartRunning();
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

        _session.StopRunning();
        _output.SetSampleBufferDelegate(null, null);
        _session.Dispose();
        _output.Dispose();
        _delegate.Dispose();
    }

    private void Disconnect(IVideoFrameConsumer consumer)
    {
        lock (_gate)
        {
            _consumers = _consumers.RemoveAll(c => ReferenceEquals(c.Consumer, consumer));
            if (_consumers.IsEmpty && _session.Running)
            {
                _session.StopRunning();
            }
        }
    }

    // Runs on the capture queue; the buffer is the camera's while the call lasts.
    private void Deliver(CMSampleBuffer sample)
    {
        using var buffer = sample.GetImageBuffer() as CVPixelBuffer;
        if (buffer is null || Mode is not { } mode)
        {
            return;
        }

        MediaTimestamp timestamp = Timestamp(sample.PresentationTimeStamp);
        VideoFormat format = new(mode.PixelFormat, (int)buffer.Width, (int)buffer.Height);
        VideoColor color = Color(buffer, mode.PixelFormat);
        nint surface = buffer.GetIOSurface()?.Handle ?? 0;
        bool locked = false;
        try
        {
            foreach ((IVideoFrameConsumer consumer, VideoConstraints constraints) in _consumers)
            {
                try
                {
                    if (
                        surface != 0
                        && constraints.Storages.Contains(VideoStorageKind.IOSurface)
                        && constraints.Device is { } device
                    )
                    {
                        VideoFrame shared = new(new VideoStorage(new IOSurfaceImage(surface, device)), format, timestamp, color: color);
                        consumer.OnFrame(in shared);
                        continue;
                    }

                    if (!locked)
                    {
                        _ = buffer.Lock(CVPixelBufferLock.ReadOnly);
                        locked = true;
                    }

                    VideoFrame mapped = Mapped(buffer, format, timestamp, color);
                    consumer.OnFrame(in mapped);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // One consumer's failure does not stop the others or the capture.
                    LogConsumerFailed(exception);
                }
            }
        }
        finally
        {
            if (locked)
            {
                _ = buffer.Unlock(CVPixelBufferLock.ReadOnly);
            }
        }
    }

    private static unsafe VideoFrame Mapped(CVPixelBuffer buffer, VideoFormat format, MediaTimestamp timestamp, VideoColor color)
    {
        int height = format.CodedSize.Height;
        if (format.PixelFormat == PixelFormat.Nv12)
        {
            int lumaStride = (int)buffer.GetBytesPerRowOfPlane(0);
            int chromaStride = (int)buffer.GetBytesPerRowOfPlane(1);
            return new VideoFrame(
                format,
                timestamp,
                new ReadOnlySpan<byte>((void*)buffer.GetBaseAddress(0), lumaStride * height),
                lumaStride,
                new ReadOnlySpan<byte>((void*)buffer.GetBaseAddress(1), chromaStride * ((height + 1) / 2)),
                chromaStride,
                color: color
            );
        }

        int stride = (int)buffer.BytesPerRow;
        return new VideoFrame(
            format,
            timestamp,
            new ReadOnlySpan<byte>((void*)buffer.BaseAddress, stride * height),
            stride,
            color: color
        );
    }

    // The host clock AVFoundation stamps with is the media clock on macOS.
    private static MediaTimestamp Timestamp(CMTime time)
    {
        MediaTime now = MediaClock.System.Now;
        if (time.IsInvalid)
        {
            return MediaTimestamp.Observed(now);
        }

        MediaTime captured = new((long)(time.Seconds * 1_000_000_000));
        return (now - captured).Duration() < TimeSpan.FromSeconds(2)
            ? MediaTimestamp.Captured(captured)
            : MediaTimestamp.Observed(now);
    }

    private static VideoColor Color(CVPixelBuffer buffer, PixelFormat format)
    {
        if (format is PixelFormat.Bgra or PixelFormat.Rgba)
        {
            return VideoColor.Srgb;
        }

        bool full = buffer.PixelFormatType == CVPixelFormatType.CV420YpCbCr8BiPlanarFullRange;
        return VideoColor.Bt709 with { Range = full ? ColorRange.Full : ColorRange.Limited };
    }

    [LoggerMessage(2590, LogLevel.Information, "Opened {Camera} in {Mode}.")]
    private partial void LogOpened(string camera, VideoInputMode mode);

    [LoggerMessage(2591, LogLevel.Warning, "A consumer failed to take a camera frame.")]
    private partial void LogConsumerFailed(Exception exception);

    private sealed class Delegate(AVFoundationVideoInput input) : AVCaptureVideoDataOutputSampleBufferDelegate
    {
        public override void DidOutputSampleBuffer(
            AVCaptureOutput captureOutput,
            CMSampleBuffer sampleBuffer,
            AVCaptureConnection connection
        )
        {
            try
            {
                input.Deliver(sampleBuffer);
            }
            finally
            {
                sampleBuffer.Dispose();
            }
        }
    }

    private sealed class Connection(AVFoundationVideoInput input, IVideoFrameConsumer consumer) : IDisposable
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
