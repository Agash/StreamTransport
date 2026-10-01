using System.Collections.Immutable;
using System.Globalization;
using Agash.StreamTransport.Linux.Vulkan;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PipeWire.NET;
using PipeWire.NET.Media;
using Vortice.Vulkan;
using PipeWireFormat = PipeWire.NET.Media.PixelFormat;
using PipeWireFrame = PipeWire.NET.Media.VideoFrame;
using PixelFormat = Agash.StreamTransport.Media.PixelFormat;
using VideoFrame = Agash.StreamTransport.Media.VideoFrame;

namespace Agash.StreamTransport.Linux.PipeWire;

/// <summary>What a <see cref="PipeWireVideoSource"/> captures.</summary>
public sealed record PipeWireVideoSourceOptions
{
    /// <summary>The node to capture, by id; null lets the session manager choose.</summary>
    public uint? TargetNodeId { get; init; }

    /// <summary>The node to capture, by name or serial; null lets the session manager choose.</summary>
    public string? TargetObject { get; init; }

    /// <summary>
    /// End the stream when its node goes away, rather than letting the daemon attach it to another:
    /// frames from somewhere else are worse than none for a source that names what it reads.
    /// </summary>
    public bool StayWithTheSource { get; init; } = true;

    /// <summary>The size to ask for; producers of other sizes still connect.</summary>
    public VideoSize PreferredSize { get; init; } = new(1920, 1080);

    /// <summary>The frame rate to ask for.</summary>
    public int PreferredFrameRate { get; init; } = 30;

    /// <summary>The GPU to share buffers on; null for the first that can.</summary>
    public GpuIdentity? Device { get; init; }

    /// <summary>The name the capture has in the graph.</summary>
    public string NodeName { get; init; } = "StreamTransport video capture";
}

/// <summary>
/// Frames of a PipeWire video node: another application's output (OBS, VTube Studio, a compositor's
/// screen capture), or a camera, which PipeWire serves as a node. A GPU producer's frames stay on the
/// GPU: when the consumer takes DMA-BUFs, the capture offers to share buffers on this source's GPU in
/// each format it imports (BGRA, and NV12 as a video producer such as a decoder makes it), in the
/// consumer's order and the layouts it can import, and each frame is the producer's own buffer while a
/// consumer's call lasts. Producers that cannot share, cameras among them, fall back to memory in any format read here.
/// A consumer that keeps a frame gets a copy, on the GPU for a shared buffer. One stream serves every
/// connected consumer and runs while any is connected.
/// </summary>
/// <remarks>The application owns the <see cref="PipeWireContext"/>, one connection to the daemon.</remarks>
public sealed partial class PipeWireVideoSource : IVideoSource, IVideoFrameRetainer, IDisposable
{
    private static readonly PixelFormat[] Readable =
    [
        PixelFormat.Bgra,
        PixelFormat.Rgba,
        PixelFormat.Nv12,
        PixelFormat.I420,
    ];

    private readonly PipeWireContext _context;
    private readonly PipeWireVideoSourceOptions _options;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Lock _copies = new();
    private ImmutableArray<IVideoFrameConsumer> _consumers = [];
    private PipeWireVideoCapture? _capture;
    private VulkanEngine? _engine;
    private DmaBufPool? _pool;
    private PipeWireFormat? _refused;

    /// <summary>A source of a PipeWire node's frames.</summary>
    /// <param name="context">A started connection to the daemon.</param>
    /// <param name="options">What to capture; defaults when null.</param>
    /// <param name="loggerFactory">Where the source logs.</param>
    public PipeWireVideoSource(
        PipeWireContext context,
        PipeWireVideoSourceOptions? options = null,
        ILoggerFactory? loggerFactory = null
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _options = options ?? new PipeWireVideoSourceOptions();
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<PipeWireVideoSource>();
    }

    /// <summary>The capture's node id once the daemon has assigned one.</summary>
    public uint? NodeId => _capture?.NodeId;

    /// <inheritdoc/>
    /// <remarks>
    /// A consumer that takes DMA-BUFs is offered the producer's shared buffers; the formats it
    /// accepts are asked for first in memory, then the rest this source reads: BGRA, RGBA, NV12 and I420.
    /// </remarks>
    public IDisposable Connect(IVideoFrameConsumer consumer, VideoConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(constraints);
        lock (_gate)
        {
            _capture ??= Start(constraints);
            _consumers = _consumers.Add(consumer);
        }

        return new Connection(this, consumer);
    }

    /// <summary>Stops capturing and disconnects every consumer.</summary>
    public void Dispose()
    {
        PipeWireVideoCapture? capture;
        lock (_gate)
        {
            capture = _capture;
            _capture = null;
            _consumers = [];
        }

        capture?.Dispose();
        lock (_copies)
        {
            _pool?.Dispose();
            _pool = null;
        }
    }

    // A kept shared-buffer frame is copied on the GPU into a picture of the source's: the producer
    // reuses its buffer as soon as the frame is handed back.
    VideoFrameLease IVideoFrameRetainer.Retain(in VideoFrame frame)
    {
        _ = frame.Storage.TryGetValue(out DmaBufImage image);
        VulkanEngine engine = _engine!;
        VideoSize size = new(frame.Format.CodedSize.Width, frame.Format.CodedSize.Height);
        PooledDmaBuf copy;
        lock (_copies)
        {
            if (_pool is not { } pool || !pool.Makes(frame.Format.PixelFormat, size))
            {
                _pool?.Dispose();
                _pool = pool = new DmaBufPool(engine, frame.Format.PixelFormat, size);
            }

            copy = pool.Rent();
        }

        try
        {
            VulkanTransfer.Copy(
                engine,
                in image,
                frame.Format.PixelFormat,
                size.Width,
                size.Height,
                copy.Planes
            );
        }
        catch
        {
            copy.Release();
            throw;
        }

        VideoFrame kept = new(
            new VideoStorage(copy.Describe(engine.Identity)),
            frame.Format,
            frame.Timestamp,
            color: frame.Color,
            orientation: frame.Orientation,
            duration: frame.Duration
        );
        return new DmaBufFrameLease(in kept, copy);
    }

    private PipeWireVideoCapture Start(VideoConstraints constraints)
    {
        List<PipeWireFormat> preferred = [];
        foreach (PixelFormat format in constraints.PixelFormats.Concat(Readable))
        {
            if (
                Readable.Contains(format)
                && PipeWireMapping.ToPipeWire(format) is { } mapped
                && !preferred.Contains(mapped)
            )
            {
                preferred.Add(mapped);
            }
        }

        DmaBufDeviceOffer? offer = constraints.Storages.Contains(VideoStorageKind.DmaBuf)
            ? Offer()
            : null;

        PipeWireVideoCapture capture = new(_context, _options.NodeName);
        capture.FrameReady += Deliver;
        try
        {
            capture.Connect(
                _options.TargetNodeId ?? PipeWireVideoCapture.AnyNode,
                [.. preferred],
                _options.TargetObject,
                stayWithTheSource: _options.StayWithTheSource,
                preferredWidth: _options.PreferredSize.Width,
                preferredHeight: _options.PreferredSize.Height,
                preferredFrameRate: _options.PreferredFrameRate,
                deviceOffers: offer is { } o ? [o] : default
            );
        }
        catch
        {
            capture.Dispose();
            throw;
        }

        LogCapturing(
            _options.TargetObject
                ?? _options.TargetNodeId?.ToString(CultureInfo.InvariantCulture)
                ?? "the default node",
            offer is not null
        );
        return capture;
    }

    // The buffers this source's GPU imports, in the layouts it can sample and copy from: BGRA as one
    // image, NV12 as one single- and one two-channel image over its planes, so in the layouts both take.
    private DmaBufDeviceOffer? Offer()
    {
        try
        {
            _engine ??= VulkanEngine.For(_options.Device);
        }
        catch (InvalidOperationException exception)
        {
            LogNoGpu(exception);
            return null;
        }

        const VkFormatFeatureFlags Needed =
            VkFormatFeatureFlags.SampledImage | VkFormatFeatureFlags.TransferSrc;
        ulong[] bgra = _engine.Modifiers(VkFormat.B8G8R8A8Unorm, Needed);
        ulong[] nv12 =
        [
            .. _engine
                .Modifiers(VkFormat.R8Unorm, Needed)
                .Intersect(_engine.Modifiers(VkFormat.R8G8Unorm, Needed)),
        ];
        List<DmaBufFormatModifiers> formats = [];
        if (bgra.Length > 0)
        {
            formats.Add(
                new DmaBufFormatModifiers(PipeWireFormat.Bgra, [.. bgra.Select(m => (long)m)])
            );
        }

        if (nv12.Length > 0)
        {
            formats.Add(
                new DmaBufFormatModifiers(PipeWireFormat.Nv12, [.. nv12.Select(m => (long)m)])
            );
        }

        if (formats.Count == 0)
        {
            return null;
        }

        ulong dev = _engine.Identity.Value;
        uint major = (uint)(((dev >> 32) & 0xfffff000) | ((dev >> 8) & 0xfff));
        uint minor = (uint)(((dev >> 12) & 0xffffff00) | (dev & 0xff));
        return new DmaBufDeviceOffer(DrmDevice.FromNumbers(major, minor), [.. formats]);
    }

    private void Disconnect(IVideoFrameConsumer consumer)
    {
        PipeWireVideoCapture? stopped = null;
        lock (_gate)
        {
            _consumers = _consumers.Remove(consumer);
            if (_consumers.IsEmpty)
            {
                stopped = _capture;
                _capture = null;
            }
        }

        stopped?.Dispose();
    }

    // Runs on the PipeWire loop thread; the frame is the producer's buffer, read in place.
    private void Deliver(PipeWireVideoCapture sender, PipeWireFrame frame)
    {
        PixelFormat? format = PipeWireMapping.ToMedia(frame.Format);
        if (
            format is { } shared
            && frame.BufferType == PipeWireBufferType.DmaBuf
            && _engine is not null
            && frame.Planes.Length > 0
        )
        {
            DeliverShared(in frame, shared);
        }
        else if (format is { } mapped && frame.HostPlaneCount >= PlaneLayout.PlaneCount(mapped))
        {
            DeliverMapped(in frame, mapped);
        }
        else if (_refused != frame.Format)
        {
            _refused = frame.Format;
            LogUnsupported(frame.Format, frame.BufferType);
        }
    }

    private void DeliverShared(in PipeWireFrame frame, PixelFormat format)
    {
        Span<DmaBufPlane> planes = stackalloc DmaBufPlane[frame.Planes.Length];
        for (int i = 0; i < planes.Length; i++)
        {
            planes[i] = new DmaBufPlane(
                (int)frame.Planes[i].Fd,
                (int)frame.Planes[i].Offset,
                frame.Planes[i].Stride
            );
        }

        // Any explicit acquire point was waited on by the capture before the frame was handed over.
        VideoFrame video = new(
            new VideoStorage(
                new DmaBufImage(planes, frame.DrmFourcc, frame.Modifier, _engine!.Identity)
            ),
            new VideoFormat(format, frame.Width, frame.Height),
            Timestamp(in frame),
            color: PipeWireMapping.ToMedia(frame.Color, format),
            retainer: this
        );
        Hand(in video);
    }

    private void DeliverMapped(in PipeWireFrame frame, PixelFormat format)
    {
        int count = PlaneLayout.PlaneCount(format);
        VideoFrame video = new(
            new VideoFormat(format, frame.Width, frame.Height),
            Timestamp(in frame),
            frame.GetHostPlane(0),
            frame.GetHostStride(0),
            count > 1 ? frame.GetHostPlane(1) : default,
            count > 1 ? frame.GetHostStride(1) : 0,
            count > 2 ? frame.GetHostPlane(2) : default,
            count > 2 ? frame.GetHostStride(2) : 0,
            color: PipeWireMapping.ToMedia(frame.Color, format)
        );
        Hand(in video);
    }

    private void Hand(in VideoFrame video)
    {
        foreach (IVideoFrameConsumer consumer in _consumers)
        {
            try
            {
                consumer.OnFrame(in video);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // One consumer's failure does not stop the others or the stream.
                LogConsumerFailed(exception);
            }
        }
    }

    private static MediaTimestamp Timestamp(in PipeWireFrame frame) =>
        PipeWireMapping.Timestamp(
            frame.PresentationTimestampNs,
            frame.QueuedTimeNs,
            MediaClock.System.Now
        );

    [LoggerMessage(
        2500,
        LogLevel.Information,
        "Capturing PipeWire video from {Target}; sharing GPU buffers: {Shared}."
    )]
    private partial void LogCapturing(string target, bool shared);

    [LoggerMessage(
        2501,
        LogLevel.Warning,
        "PipeWire video in {Format} ({BufferType}) is not read by this source; its frames are skipped."
    )]
    private partial void LogUnsupported(PipeWireFormat format, PipeWireBufferType bufferType);

    [LoggerMessage(2502, LogLevel.Warning, "A consumer failed to take a PipeWire frame.")]
    private partial void LogConsumerFailed(Exception exception);

    [LoggerMessage(
        2503,
        LogLevel.Warning,
        "No GPU here shares DMA-BUFs; PipeWire video is read from memory."
    )]
    private partial void LogNoGpu(Exception exception);

    private sealed class Connection(PipeWireVideoSource source, IVideoFrameConsumer consumer)
        : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                source.Disconnect(consumer);
            }
        }
    }
}
