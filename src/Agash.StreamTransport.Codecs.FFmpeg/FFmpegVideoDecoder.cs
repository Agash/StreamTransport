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
        return new FFmpegFrameLease(in frame, current.Clone());
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
        // Decoded pictures are Y'CbCr; a stream tagged RGB (a sender that signalled its source's sRGB)
        // is read as BT.709, so conversions downstream still have a matrix to use.
        VideoColor color = Formats.FromFFmpeg(decoded).ConvertedTo(PixelFormat.Nv12);
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
        if (!_mapped.TryGetDrmFrame(out FF.DrmFrameDescriptor descriptor))
        {
            throw new NotSupportedException(
                "The decoder's surface did not map to a DRM PRIME image."
            );
        }

        // VA-API exports YUV as one layer per plane (R8 luma, GR88 chroma); the planes of every layer,
        // in order, are the planes of the one image.
        Span<DmaBufPlane> planes = stackalloc DmaBufPlane[VideoPlanes.MaximumPlanes];
        int planeCount = 0;
        for (int layer = 0; layer < descriptor.LayerCount; layer++)
        {
            for (int i = 0; i < descriptor.GetPlaneCount(layer); i++)
            {
                FF.DrmPlane plane = descriptor.GetPlane(layer, i);
                FF.DrmObject dmaBuf = descriptor.GetObject(plane.ObjectIndex);
                planes[planeCount++] = new DmaBufPlane(
                    dmaBuf.FileDescriptor,
                    (int)plane.Offset,
                    (int)plane.Pitch
                );
            }
        }

        VideoFormat format = SurfaceFormat(decoded);
        uint drmFormat =
            descriptor.LayerCount == 1
                ? descriptor.GetLayerFormat(0)
                : DrmFourcc(format.PixelFormat);

        _current = _mapped;
        consumer.OnFrame(
            new VideoFrame(
                new DmaBufImage(
                    planes[..planeCount],
                    drmFormat,
                    descriptor.GetObject(0).Modifier,
                    _identity ?? default
                ),
                format,
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
        consumer.OnFrame(FFmpegFrames.View(source, format, timestamp, color, this));
    }

    // The DRM fourcc of a multi-planar format as one image.
    private static uint DrmFourcc(PixelFormat format) =>
        format switch
        {
            PixelFormat.Nv12 => 0x3231564E, // 'NV12'
            PixelFormat.P010 => 0x30313050, // 'P010'
            _ => throw new NotSupportedException($"{format} has no multi-planar DRM format."),
        };

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
