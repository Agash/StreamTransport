using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
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

    // Pictures for downloads: a fresh one each frame would be megabytes of new pages faulted in on the
    // first write.
    private readonly FF.FramePool _downloads = new();
    private readonly FF.Frame _converted = new();
    private readonly FF.Frame _mapped = new();
    private readonly TimestampRing _timestamps = new();
    private FF.Scaler? _scaler;
    private FF.Frame? _current;
    private nint _surfacePool;
    private PixelFormat _surfaceFormat;
    private VideoSize _surfaceSize;
    private long _lastPts = long.MinValue;
    private bool _disposed;

    // DMA-BUF frames are handed over by a worker. Mapping a surface waits for its decode (libva requires
    // vaSyncSurface before a surface is read, and FFmpeg's map calls it), so mapped on the decoding
    // thread each decode would wait for the one before; on the worker the next access unit is already
    // decoding while the last one finishes. Two in flight is enough to overlap them, and the decoder's
    // pool has a surface more for each.
    private const int InFlight = 2;
    private readonly Lock _deliveryGate = new();
    private readonly SemaphoreSlim _free = new(InFlight, InFlight);
    private readonly SemaphoreSlim _ready = new(0, InFlight + 1);
    private readonly Pending[] _pending = new Pending[InFlight];
    private int _pendingHead;
    private int _pendingCount;
    private Thread? _deliverer;
    private Exception? _deliveryFault;

    // Vulkan pictures shared with one GPU submission each instead of FFmpeg's map (made on the worker).
    private FF.VulkanDmaBufExporter? _exporter;

    public FFmpegVideoDecoder(
        FF.Codec codec,
        VideoCodecId id,
        FF.HardwareDevice? device,
        GpuIdentity? identity,
        VideoStorageKind output,
        PixelFormat cpuFormat,
        ImmutableArray<ulong> drmModifiers,
        int heldFrames,
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
                DrmModifiers = drmModifiers,
                ExtraHardwareFrames =
                    heldFrames + (output == VideoStorageKind.DmaBuf ? InFlight : 0),
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
            ThrowDeliveryFault();
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

    public void Flush(IVideoFrameConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Every slot free is every frame handed over.
            for (int i = 0; i < InFlight; i++)
            {
                _free.Wait();
            }

            _ = _free.Release(InFlight);
            if (OperatingSystem.IsLinux())
            {
                // The worker is idle with every slot free, so the exporter is this thread's now.
                _exporter?.Flush();
            }

            ThrowDeliveryFault();
        }
    }

    // Called by a consumer inside OnFrame: the frame it holds is the one being delivered.
    public VideoFrameLease Retain(in VideoFrame frame)
    {
        FF.Frame current =
            _current
            ?? throw new InvalidOperationException("Frames are retained only while delivered.");
        return FFmpegFrameLease.Of(in frame, current);
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

            // Frames already decoded are still handed over; the worker stops once none are left.
            // A wake with nothing queued tells the worker to stop.
            _ = _ready.Release();
            _deliverer?.Join();
            if (OperatingSystem.IsLinux())
            {
                _exporter?.Dispose();
            }

            _free.Dispose();
            _ready.Dispose();
            foreach (Pending pending in _pending)
            {
                pending.Frame?.Dispose();
            }

            _decoder.Dispose();
            _device?.Dispose();
            _scaler?.Dispose();
            _packet.Dispose();
            _decoded.Dispose();
            _downloaded.Dispose();
            _downloads.Dispose();
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

        // Handed over by the worker, which holds the frame being delivered itself.
        if (_output == VideoStorageKind.DmaBuf && OperatingSystem.IsLinux())
        {
            Hand(decoded, timestamp, color, consumer);
            return;
        }

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

    // Queues a decoded surface for the worker, waiting while two are already on their way.
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private void Hand(
        FF.Frame decoded,
        MediaTimestamp timestamp,
        VideoColor color,
        IVideoFrameConsumer consumer
    )
    {
        _free.Wait();
        lock (_deliveryGate)
        {
            int slot = (_pendingHead + _pendingCount) % InFlight;
            FF.Frame frame = _pending[slot].Frame ?? new FF.Frame();
            frame.Reference(decoded);
            _pending[slot] = new Pending(frame, timestamp, color, consumer);
            _pendingCount++;
        }

        _deliverer ??= StartDeliverer();
        _ = _ready.Release();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private Thread StartDeliverer()
    {
        Thread thread = new(Deliver)
        {
            IsBackground = true,
            Name = $"{Info.ImplementationName} frames",
        };
        thread.Start();
        return thread;
    }

    // The worker: maps each surface in order, hands it over, and gives the slot back.
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private void Deliver()
    {
        while (true)
        {
            _ready.Wait();
            Pending pending;
            lock (_deliveryGate)
            {
                // Woken with nothing queued only to stop: what was queued before is handed over first.
                if (_pendingCount == 0)
                {
                    return;
                }

                pending = _pending[_pendingHead];
            }

            try
            {
                DeliverDmaBuf(pending.Frame!, pending.Timestamp, pending.Color, pending.Consumer);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The next Decode or Flush throws it, where a failed frame asks for a keyframe.
                LogDeliveryFailed(exception);
                _ = Interlocked.CompareExchange(ref _deliveryFault, exception, null);
            }
            finally
            {
                _current = null;
                _mapped.Reset();
                pending.Frame!.Reset();
                lock (_deliveryGate)
                {
                    _pendingHead = (_pendingHead + 1) % InFlight;
                    _pendingCount--;
                }

                _ = _free.Release();
            }
        }
    }

    private void ThrowDeliveryFault()
    {
        if (Interlocked.Exchange(ref _deliveryFault, null) is { } fault)
        {
            ExceptionDispatchInfo.Throw(fault);
        }
    }

    [LoggerMessage(
        EventId = 1022,
        Level = LogLevel.Warning,
        Message = "A decoded frame could not be handed over"
    )]
    private partial void LogDeliveryFailed(Exception exception);

    // A decoded surface on its way to the worker, with what its delivery needs.
    private readonly record struct Pending(
        FF.Frame? Frame,
        MediaTimestamp Timestamp,
        VideoColor Color,
        IVideoFrameConsumer Consumer
    );

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
        if (
            decoded.PixelFormat != FF.PixelFormat.Vulkan
            || !(_exporter ??= new FF.VulkanDmaBufExporter()).Export(decoded, _mapped)
        )
        {
            _mapped.PixelFormat = FF.PixelFormat.DrmPrime;
            decoded.MapTo(_mapped, FF.HardwareMapAccess.Read);
        }

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
            decoded.DownloadTo(_downloaded, _downloads);
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

    // The surfaces' format and size come from the frame's pool, which changes only when the decoder makes
    // a new one; read through a wrapper only then, as the wrapper takes a reference and a handle.
    private unsafe VideoFormat SurfaceFormat(FF.Frame frame)
    {
        nint pool = (nint)frame.NativePointer->hw_frames_ctx->data;
        if (pool != _surfacePool)
        {
            using var frames = FF.HardwareFramePool.Of(frame);
            _surfaceFormat = Formats.FromFFmpeg(frames.SoftwareFormat) ?? PixelFormat.Nv12;
            _surfaceSize = new VideoSize(frames.Width, frames.Height);
            _surfacePool = pool;
        }

        return new VideoFormat(
            _surfaceFormat,
            _surfaceSize,
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
