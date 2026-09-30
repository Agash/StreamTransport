using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using static FFmpeg.Interop.D3D11VAExtensions;
using static FFmpeg.Interop.D3D12VAExtensions;
using static FFmpeg.Interop.DrmExtensions;
using static FFmpeg.Interop.VideoToolboxExtensions;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Codecs.FFmpeg;

// One FFmpeg decoder for every backend. Decoded frames are handed out as views over FFmpeg's own
// buffers or surfaces; retaining one takes a reference on the FFmpeg frame, never a copy. A frame
// leaves the GPU only when the consumer takes system memory, in which case it is downloaded once.
internal sealed partial class FFmpegVideoDecoder : IVideoDecoder, IVideoFrameRetainer
{
    private static readonly FF.Rational TimeBase = new(1, 90_000);

    private readonly FF.Decoder _decoder;
    private readonly FF.HardwareDevice? _device;
    private readonly VideoCodecId _codec;
    private readonly VideoStorageKind _output;
    private readonly PixelFormat _cpuFormat;
    private readonly GpuIdentity? _identity;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly FF.Packet _packet = new();
    private readonly FF.Frame _decoded = new();
    private readonly FF.Frame _downloaded = new();
    private readonly FF.Frame _converted = new();
    private readonly FF.Frame _mapped = new();
    private readonly TimestampRing _timestamps = new();
    private FF.Scaler? _scaler;
    private FF.Frame? _current;
    private long _lastPts = long.MinValue;
    private bool _disposed;

    public FFmpegVideoDecoder(
        FF.Codec codec,
        VideoCodecId id,
        FF.HardwareDevice? device,
        GpuIdentity? identity,
        VideoStorageKind output,
        PixelFormat cpuFormat,
        VideoDecoderInfo info,
        ILogger logger
    )
    {
        _codec = id;
        _device = device;
        _identity = identity;
        _output = output;
        _cpuFormat = cpuFormat;
        _logger = logger;
        Info = info;
        _decoder = FF.Decoder.Create(
            codec,
            new FF.DecoderOptions
            {
                HardwareDevice = device,
                LowDelay = true,
                PacketTimeBase = TimeBase,
            }
        );
    }

    public VideoDecoderInfo Info { get; }

    public void Decode(in EncodedVideoFrame frame, IVideoFrameConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        if (frame.Codec != _codec)
        {
            throw new ArgumentException(
                $"{Info.ImplementationName} decodes {_codec}, not {frame.Codec}.",
                nameof(frame)
            );
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long pts = (long)(
                (Int128)frame.Timestamp.Time.Nanoseconds * TimeBase.Denominator / 1_000_000_000
            );
            if (pts <= _lastPts)
            {
                pts = _lastPts + 1;
            }

            _lastPts = pts;
            _timestamps.Add(pts, frame.Timestamp);
            _packet.CopyFrom(frame.Data);
            _packet.PresentationTimestamp = pts;
            _packet.TimeBase = TimeBase;
            foreach (FF.Frame decoded in _decoder.Decode(_packet, _decoded))
            {
                Deliver(decoded, consumer);
            }
        }
    }

    // Called by a consumer inside OnFrame: the frame it holds is the one being delivered.
    public VideoFrameLease Retain(in VideoFrame frame)
    {
        FF.Frame current =
            _current
            ?? throw new InvalidOperationException("Frames are retained only while delivered.");
        return new DecodedFrameLease(in frame, current.Clone());
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
            _decoder.Dispose();
            _device?.Dispose();
            _scaler?.Dispose();
            _packet.Dispose();
            _decoded.Dispose();
            _downloaded.Dispose();
            _converted.Dispose();
            _mapped.Dispose();
        }
    }

    private void Deliver(FF.Frame decoded, IVideoFrameConsumer consumer)
    {
        MediaTimestamp timestamp = _timestamps.Take(decoded.PresentationTimestamp ?? _lastPts);
        VideoColor color = Formats.FromFFmpeg(decoded);
        try
        {
            switch (_output)
            {
                case VideoStorageKind.D3D12
                    when OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)
                        && decoded.TryGetD3D12Texture(out FF.D3D12Texture d3d12):
                    _current = decoded;
                    consumer.OnFrame(
                        new VideoFrame(
                            new D3D12Image(
                                d3d12.Resource,
                                d3d12.Subresource,
                                _identity!.Value,
                                new D3D12Sync(Fence: d3d12.Fence, Value: d3d12.FenceValue)
                            ),
                            SurfaceFormat(decoded),
                            timestamp,
                            color: color,
                            retainer: this
                        )
                    );
                    break;
                case VideoStorageKind.D3D11
                    when OperatingSystem.IsWindowsVersionAtLeast(6, 1)
                        && decoded.TryGetD3D11Texture(out FF.D3D11Texture d3d11):
                    _current = decoded;
                    consumer.OnFrame(
                        new VideoFrame(
                            new D3D11Image(d3d11.Texture, d3d11.ArraySlice, _identity!.Value),
                            SurfaceFormat(decoded),
                            timestamp,
                            color: color,
                            retainer: this
                        )
                    );
                    break;
                case VideoStorageKind.IOSurface
                    when OperatingSystem.IsMacOS() && decoded.TryGetIOSurface(out nint surface):
                    _current = decoded;
                    consumer.OnFrame(
                        new VideoFrame(
                            new IOSurfaceImage(surface, _identity ?? default),
                            SurfaceFormat(decoded),
                            timestamp,
                            color: color,
                            retainer: this
                        )
                    );
                    break;
                case VideoStorageKind.DmaBuf when OperatingSystem.IsLinux():
                    DeliverDmaBuf(decoded, timestamp, color, consumer);
                    break;
                default:
                    DeliverCpu(decoded, timestamp, color, consumer);
                    break;
            }
        }
        finally
        {
            _current = null;
        }
    }

    // A surface mapped as DRM PRIME, so its DMA-BUFs can be shared.
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private void DeliverDmaBuf(
        FF.Frame decoded,
        MediaTimestamp timestamp,
        VideoColor color,
        IVideoFrameConsumer consumer
    )
    {
        _mapped.Reset();
        _mapped.PixelFormat = FF.PixelFormat.DrmPrime;
        decoded.MapTo(_mapped, FF.HardwareMapAccess.Read);
        if (
            !_mapped.TryGetDrmFrame(out FF.DrmFrameDescriptor descriptor)
            || descriptor.LayerCount != 1
        )
        {
            throw new NotSupportedException(
                "The decoder's surface is not a single-layer DRM PRIME image."
            );
        }

        int planeCount = descriptor.GetPlaneCount(0);
        Span<DmaBufPlane> planes = stackalloc DmaBufPlane[planeCount];
        for (int i = 0; i < planeCount; i++)
        {
            FF.DrmPlane plane = descriptor.GetPlane(0, i);
            FF.DrmObject dmaBuf = descriptor.GetObject(plane.ObjectIndex);
            planes[i] = new DmaBufPlane(dmaBuf.FileDescriptor, (int)plane.Offset, (int)plane.Pitch);
        }

        _current = _mapped;
        consumer.OnFrame(
            new VideoFrame(
                new DmaBufImage(
                    planes,
                    descriptor.GetLayerFormat(0),
                    descriptor.GetObject(0).Modifier,
                    _identity ?? default
                ),
                SurfaceFormat(decoded),
                timestamp,
                color: color,
                retainer: this
            )
        );
    }

    // System memory: FFmpeg's buffers as they are when the format is the consumer's, else converted.
    private unsafe void DeliverCpu(
        FF.Frame decoded,
        MediaTimestamp timestamp,
        VideoColor color,
        IVideoFrameConsumer consumer
    )
    {
        FF.Frame source = decoded;
        if (decoded.IsHardwareFrame)
        {
            _downloaded.Reset();
            decoded.TransferTo(_downloaded);
            _downloaded.CopyPropertiesFrom(decoded);
            source = _downloaded;
        }

        if (Formats.FromFFmpeg(source.PixelFormat) != _cpuFormat)
        {
            _scaler ??= new FF.Scaler();
            _converted.Reset();
            _converted.Width = source.Width;
            _converted.Height = source.Height;
            _converted.PixelFormat = Formats.ToFFmpeg(_cpuFormat);
            _scaler.Scale(source, _converted);
            _converted.CopyPropertiesFrom(source);
            source = _converted;
        }

        _current = source;
        VideoFormat format = new(_cpuFormat, source.Width, source.Height);
        consumer.OnFrame(Planes(source, format, timestamp, color, this));
    }

    internal static unsafe VideoFrame Planes(
        FF.Frame frame,
        VideoFormat format,
        MediaTimestamp timestamp,
        VideoColor color,
        IVideoFrameRetainer? retainer
    )
    {
        var native = frame.NativePointer;
        int count = PlaneLayout.PlaneCount(format.PixelFormat);
        ReadOnlySpan<byte> Plane(int index) =>
            index < count
                ? new ReadOnlySpan<byte>(
                    native->data[index],
                    native->linesize[index]
                        * PlaneLayout.PlaneRows(format.PixelFormat, index, frame.Height)
                )
                : default;

        return new VideoFrame(
            format,
            timestamp,
            Plane(0),
            native->linesize[0],
            Plane(1),
            count > 1 ? native->linesize[1] : 0,
            Plane(2),
            count > 2 ? native->linesize[2] : 0,
            color,
            retainer: retainer
        );
    }

    private static VideoFormat SurfaceFormat(FF.Frame frame)
    {
        using var pool = FF.HardwareFramePool.Of(frame);
        PixelFormat format = Formats.FromFFmpeg(pool.SoftwareFormat) ?? PixelFormat.Nv12;
        return new VideoFormat(
            format,
            new VideoSize(pool.Width, pool.Height),
            new VideoRect(0, 0, frame.Width, frame.Height)
        );
    }

    // Capture timestamps by the pts the packets went into FFmpeg with.
    private sealed class TimestampRing
    {
        private readonly (long Pts, MediaTimestamp Timestamp)[] _entries = new (
            long,
            MediaTimestamp
        )[64];
        private int _next;

        public void Add(long pts, MediaTimestamp timestamp)
        {
            _entries[_next] = (pts, timestamp);
            _next = (_next + 1) % _entries.Length;
        }

        public MediaTimestamp Take(long pts)
        {
            foreach ((long entryPts, MediaTimestamp timestamp) in _entries)
            {
                if (entryPts == pts)
                {
                    return timestamp;
                }
            }

            return MediaTimestamp.Observed(
                new MediaTime(pts * 1_000_000_000 / TimeBase.Denominator)
            );
        }
    }
}

// A decoded frame kept by reference: the FFmpeg frame, and with it its buffers or surface, stays alive
// until the lease is disposed.
internal sealed class DecodedFrameLease : VideoFrameLease, IVideoFrameRetainer
{
    private readonly FF.Frame _frame;

    public DecodedFrameLease(in VideoFrame frame, FF.Frame reference)
        : base(
            frame.Storage,
            frame.Format,
            frame.Timestamp,
            frame.Color,
            frame.Orientation,
            frame.Duration
        )
    {
        _frame = reference;
    }

    protected override IVideoFrameRetainer KeepAgain => this;

    public VideoFrameLease Retain(in VideoFrame frame) =>
        new DecodedFrameLease(in frame, _frame.Clone());

    protected override unsafe ReadOnlySpan<byte> GetPlane(int index)
    {
        var native = _frame.NativePointer;
        return new ReadOnlySpan<byte>(
            native->data[index],
            native->linesize[index]
                * PlaneLayout.PlaneRows(Format.PixelFormat, index, _frame.Height)
        );
    }

    protected override void Release() => _frame.Dispose();
}
