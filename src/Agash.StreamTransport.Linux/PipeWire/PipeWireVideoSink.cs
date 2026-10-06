using Agash.StreamTransport.Linux.Vulkan;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PipeWire.NET;
using PipeWire.NET.Media;
using Vortice.Vulkan;
using PipeWirePlane = PipeWire.NET.Media.VideoPlane;
using PixelFormat = Agash.StreamTransport.Media.PixelFormat;
using VideoFrame = Agash.StreamTransport.Media.VideoFrame;

namespace Agash.StreamTransport.Linux.PipeWire;

/// <summary>Where a <see cref="PipeWireVideoSink"/> publishes.</summary>
public sealed record PipeWireVideoSinkOptions
{
    /// <summary>The node to publish into, by id; null leaves routing to the session manager.</summary>
    public uint? TargetNodeId { get; init; }

    /// <summary>
    /// Let the session manager route the node. Off publishes it unlinked, for an application or
    /// consumer that links it deliberately. A node of GPU frames is always left to its consumer to link.
    /// </summary>
    public bool AutoConnect { get; init; } = true;

    /// <summary>The frame rate the node announces.</summary>
    public int FrameRate { get; init; } = 30;
}

/// <summary>
/// Publishes frames as a PipeWire video node, which OBS and every other PipeWire consumer sees as a
/// camera. Frames are pushed as they arrive into a free buffer of the node, with at most one waiting
/// for the consumer: a frame offered while the last has not been taken is skipped. GPU frames stay on
/// the GPU: DMA-BUF frames (BGRA, RGBA or NV12) go into buffers shared with the consumer, drawn there by
/// a processor (<see cref="Render{TState}"/>) or copied there once on the GPU; a consumer that cannot
/// import a shared buffer gets them read back into memory. Frames in memory (BGRA, RGBA, NV12 or I420)
/// are written once into the daemon's buffer. The node takes the size, format, colour and storage of the
/// first frame, declares the colour so consumers convert it correctly, and is made again when any of
/// them change.
/// </summary>
public sealed partial class PipeWireVideoSink : IVideoSink, IDisposable
{
    private readonly PipeWireContext _context;
    private readonly string _name;
    private readonly PipeWireVideoSinkOptions _options;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, VulkanImage[]> _shared = [];
    private PipeWireVideoOutput? _output;
    private VideoFormat _format;
    private VideoColor _color;
    private bool _gpu;
    private VulkanEngine? _engine;
    private VulkanImage[]? _lent;
    private bool _disposed;

    /// <summary>A PipeWire video node.</summary>
    /// <param name="context">A started connection to the daemon.</param>
    /// <param name="name">The node's name, as consumers list it.</param>
    /// <param name="options">Where to publish; defaults when null.</param>
    /// <param name="loggerFactory">Where the sink logs.</param>
    public PipeWireVideoSink(
        PipeWireContext context,
        string name,
        PipeWireVideoSinkOptions? options = null,
        ILoggerFactory? loggerFactory = null
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _context = context;
        _name = name;
        _options = options ?? new PipeWireVideoSinkOptions();
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<PipeWireVideoSink>();
    }

    /// <inheritdoc/>
    /// <remarks>DMA-BUF frames in BGRA, RGBA or NV12 in rows; memory frames in any of the four.</remarks>
    public VideoConstraints Constraints { get; } =
        new(
            [VideoStorageKind.DmaBuf, VideoStorageKind.Cpu],
            [PixelFormat.Bgra, PixelFormat.Rgba, PixelFormat.Nv12, PixelFormat.I420],
            DrmModifiers: [VulkanEngine.LinearModifier]
        );

    /// <summary>The node's id once the daemon has assigned one.</summary>
    public uint? NodeId => _output?.NodeId;

    /// <summary>Completes when the node has an id, which a consumer targets it by.</summary>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>The node id.</returns>
    /// <exception cref="InvalidOperationException">No frame has been published yet.</exception>
    public Task<uint> WaitForNodeIdAsync(CancellationToken cancellationToken = default) =>
        (
            _output ?? throw new InvalidOperationException("The node exists from the first frame.")
        ).WaitForNodeIdAsync(cancellationToken);

    /// <inheritdoc/>
    public void OnFrame(in VideoFrame frame)
    {
        bool gpu = frame.Storage.Kind == VideoStorageKind.DmaBuf;
        PixelFormat pixels = frame.Format.PixelFormat;
        if (
            !Constraints.Accepts(frame.Storage, pixels)
            || (gpu && pixels is not (PixelFormat.Bgra or PixelFormat.Rgba or PixelFormat.Nv12))
        )
        {
            throw new ArgumentException(
                $"The sink takes BGRA, RGBA or NV12 on DMA-BUFs, or those and I420 in memory; a processor makes them. The frame is {pixels} {frame.Storage.Kind}.",
                nameof(frame)
            );
        }

        PipeWireVideoOutput output;
        VideoFormat format;
        VulkanEngine? engine;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            format = new(pixels, frame.Format.VisibleRect.Width, frame.Format.VisibleRect.Height);
            if (_output is null || format != _format || frame.Color != _color || gpu != _gpu)
            {
                Restart(format, frame.Color, gpu, frame.Storage.Device);
            }

            output = _output!;
            engine = _engine;
        }

        if (
            gpu
            && Render(
                format,
                new Copying(this, frame),
                static (in VideoTarget _, scoped in Copying copying) =>
                    copying.Sink.CopyInto(in copying.Frame)
            ) != VideoRenderResult.Unavailable
        )
        {
            return;
        }

        // A consumer reading memory, or a node of memory frames: written once into the daemon's buffer.
        // Before a consumer settles there is nothing to write into, and the frame is skipped.
        if (!output.TryBeginFrame(out PipeWireOutputFrame written))
        {
            return;
        }

        using (written)
        {
            if (written.Pixels.IsEmpty)
            {
                return;
            }

            if (gpu)
            {
                ReadBack(engine!, in frame, format, written.Pixels, written.Stride);
            }
            else
            {
                Write(in frame, format, written.Pixels, written.Stride);
            }

            written.Publish();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Lends a free shared buffer once a consumer has settled on shared buffers for frames of this
    /// format; unavailable before then and while it reads memory. One frame waits for the consumer at
    /// most, so a frame offered while the last one has not been taken is skipped. The renderer finishes
    /// drawing before it returns.
    /// <para>
    /// Lock order: the PipeWire loop thread takes this sink's lock under the loop lock (to back and
    /// release buffers), so the loop lock, which dequeuing and publishing take, is taken here only while
    /// this sink's lock is not held.
    /// </para>
    /// </remarks>
    public VideoRenderResult Render<TState>(
        VideoFormat format,
        scoped in TState state,
        VideoTargetRenderer<TState> render
    )
        where TState : allows ref struct
    {
        ArgumentNullException.ThrowIfNull(render);
        PipeWireVideoOutput output;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (
                _output is null
                || !_gpu
                || format.PixelFormat != _format.PixelFormat
                || format.CodedSize != _format.CodedSize
            )
            {
                return VideoRenderResult.Unavailable;
            }

            output = _output;
        }

        if (!output.TryBeginFrame(out PipeWireOutputFrame frame))
        {
            return output.SharesBuffers ? VideoRenderResult.Skipped : VideoRenderResult.Unavailable;
        }

        using (frame)
        {
            lock (_gate)
            {
                if (
                    output != _output
                    || !_shared.TryGetValue(frame.BufferIndex, out VulkanImage[]? images)
                )
                {
                    return VideoRenderResult.Unavailable;
                }

                // Drawn holding the lock, so the buffer's images cannot be released meanwhile.
                VideoTarget target = new(new VideoStorage(Describe(images)), format);
                _lent = images;
                try
                {
                    if (!render(in target, in state))
                    {
                        return VideoRenderResult.Unavailable;
                    }
                }
                finally
                {
                    _lent = null;
                }
            }

            frame.Publish();
            return VideoRenderResult.Rendered;
        }
    }

    /// <summary>Stops publishing and removes the node.</summary>
    public void Dispose()
    {
        PipeWireVideoOutput? output;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            output = _output;
            _output = null;
        }

        output?.Dispose();
        lock (_gate)
        {
            DropShared();
        }
    }

    // A node of the frame's size, format, colour and storage; called holding the lock.
    private void Restart(VideoFormat format, VideoColor color, bool gpu, GpuIdentity? device)
    {
        _output?.Dispose();
        _output = null;
        DropShared();
        _format = format;
        _color = color;
        _gpu = gpu;
        PipeWireVideoOutput output = new(
            _context,
            _name,
            format.CodedSize.Width,
            format.CodedSize.Height,
            PipeWireMapping.ToPipeWire(format.PixelFormat)!.Value,
            _options.FrameRate,
            PipeWireMapping.ToPipeWire(color)
        )
        {
            PushFrames = true,
        };
        try
        {
            if (gpu)
            {
                _engine = VulkanEngine.For(device);
                output.AllocateDmaBuf += Allocate;
                output.ReleaseDmaBuf += ReleaseShared;

                // A consumer that cannot import a shared buffer gets frames read back into memory.
                output.HostMemoryFallback = true;
                output.ConnectDmaBuf([
                    new DmaBufDeviceOffer(
                        Drm(_engine.Identity),
                        PipeWireMapping.ToPipeWire(format.PixelFormat)!.Value,
                        [(long)VulkanEngine.LinearModifier]
                    ),
                ]);
            }
            else
            {
                output.Connect(
                    _options.TargetNodeId ?? PipeWireVideoOutput.AnyNode,
                    autoConnect: _options.AutoConnect,
                    driver: true
                );
            }
        }
        catch
        {
            output.Dispose();
            throw;
        }

        _output = output;
        LogPublishing(
            _name,
            format.PixelFormat,
            format.CodedSize.Width,
            format.CodedSize.Height,
            gpu
        );
    }

    // Runs on the PipeWire loop thread: backs a pool buffer with images of ours, exported per plane.
    private int Allocate(
        PipeWireVideoOutput sender,
        int bufferIndex,
        int width,
        int height,
        ulong modifier,
        DrmDevice? device,
        Span<PipeWirePlane> planes
    )
    {
        lock (_gate)
        {
            VulkanEngine engine = _engine!;
            const VkImageUsageFlags usage =
                VkImageUsageFlags.TransferDst | VkImageUsageFlags.TransferSrc;
            // One DMA-BUF per picture, its planes at their offsets: importers such as VA-API map only
            // pictures made of a single object.
            VulkanImage[] images = _format.PixelFormat switch
            {
                PixelFormat.Nv12 => VulkanImage.ExportPicture(
                    engine,
                    [
                        (VkFormat.R8Unorm, width, height),
                        (VkFormat.R8G8Unorm, width / 2, height / 2),
                    ],
                    usage
                ),
                PixelFormat.Rgba => VulkanImage.ExportPicture(
                    engine,
                    [(VkFormat.R8G8B8A8Unorm, width, height)],
                    usage
                ),
                _ => VulkanImage.ExportPicture(
                    engine,
                    [(VkFormat.B8G8R8A8Unorm, width, height)],
                    usage
                ),
            };
            if (_shared.Remove(bufferIndex, out VulkanImage[]? previous))
            {
                Dispose(previous);
            }

            _shared[bufferIndex] = images;
            for (int i = 0; i < images.Length; i++)
            {
                DmaBufPlane plane = images[i].Plane;
                planes[i] = new PipeWirePlane(
                    plane.Fd,
                    (uint)plane.Offset,
                    plane.Stride,
                    (uint)(plane.Stride * images[i].Height)
                );
            }

            return images.Length;
        }
    }

    // A GPU frame copied into the buffer being lent, for a frame no processor drew there; called holding
    // the lock.
    private bool CopyInto(in VideoFrame frame)
    {
        _ = frame.Storage.TryGetValue(out DmaBufImage image);
        VulkanTransfer.Copy(
            _engine!,
            in image,
            _format.PixelFormat,
            frame.Format.CodedSize.Width,
            frame.Format.CodedSize.Height,
            _lent!
        );
        return true;
    }

    // A GPU frame handed to the sink, copied into a lent buffer.
    private readonly ref struct Copying(PipeWireVideoSink sink, VideoFrame frame)
    {
        public readonly PipeWireVideoSink Sink = sink;

        public readonly VideoFrame Frame = frame;
    }

    // A buffer's images as DMA-BUF storage on the sink's GPU.
    private DmaBufImage Describe(VulkanImage[] images)
    {
        Span<DmaBufPlane> planes = stackalloc DmaBufPlane[images.Length];
        for (int i = 0; i < images.Length; i++)
        {
            planes[i] = images[i].Plane;
        }

        return new DmaBufImage(
            planes,
            DrmFourcc.Of(_format.PixelFormat),
            VulkanEngine.LinearModifier,
            _engine!.Identity
        );
    }

    private void ReleaseShared(PipeWireVideoOutput sender, int bufferIndex)
    {
        lock (_gate)
        {
            if (_shared.Remove(bufferIndex, out VulkanImage[]? images))
            {
                Dispose(images);
            }
        }
    }

    // Called holding the lock.
    private void DropShared()
    {
        foreach (VulkanImage[] images in _shared.Values)
        {
            Dispose(images);
        }

        _shared.Clear();
    }

    // The visible picture of a frame in memory into the daemon's buffer: planes one after another, each
    // at the first plane's stride scaled as the plane's rows are to the first's.
    private static void Write(
        in VideoFrame frame,
        VideoFormat format,
        Span<byte> pixels,
        int stride
    )
    {
        VideoSize size = Size(format);
        var packed = PlaneLayout.Packed(format.PixelFormat, size);
        _ = frame.Storage.TryGetValue(out CpuImage image);
        VideoRect visible = frame.Format.VisibleRect;
        int offset = 0;
        for (int plane = 0; plane < packed.Count; plane++)
        {
            (int x, int y) = PlaneOrigin(format.PixelFormat, plane, visible);
            int sourceStride = image.Planes[plane].Stride;
            ReadOnlySpan<byte> source = frame.GetPlane(plane);
            int planeStride = stride * packed[plane].Stride / packed[0].Stride;
            int rowBytes = packed[plane].Stride;
            int rows = PlaneLayout.PlaneRows(format.PixelFormat, plane, size.Height);
            for (int row = 0; row < rows; row++)
            {
                source
                    .Slice(((y + row) * sourceStride) + x, rowBytes)
                    .CopyTo(pixels[(offset + (row * planeStride))..]);
            }

            offset += rows * planeStride;
        }
    }

    // A GPU frame read back for a consumer reading memory, into the daemon's buffer as Write lays it.
    private static void ReadBack(
        VulkanEngine engine,
        in VideoFrame frame,
        VideoFormat format,
        Span<byte> pixels,
        int stride
    )
    {
        _ = frame.Storage.TryGetValue(out DmaBufImage image);
        VideoSize size = Size(format);
        byte[][] planes = VulkanTransfer.Read(
            engine,
            in image,
            format.PixelFormat,
            size.Width,
            size.Height
        );
        var packed = PlaneLayout.Packed(format.PixelFormat, size);
        int offset = 0;
        for (int plane = 0; plane < planes.Length; plane++)
        {
            int planeStride = stride * packed[plane].Stride / packed[0].Stride;
            int rowBytes = packed[plane].Stride;
            int rows = PlaneLayout.PlaneRows(format.PixelFormat, plane, size.Height);
            for (int row = 0; row < rows; row++)
            {
                planes[plane]
                    .AsSpan(row * rowBytes, rowBytes)
                    .CopyTo(pixels[(offset + (row * planeStride))..]);
            }

            offset += rows * planeStride;
        }
    }

    private static void Dispose(VulkanImage[] images)
    {
        foreach (VulkanImage image in images)
        {
            image.Dispose();
        }
    }

    // The DRM device a dev_t names, split as glibc's major and minor do.
    private static DrmDevice Drm(GpuIdentity identity)
    {
        ulong dev = identity.Value;
        uint major = (uint)(((dev >> 32) & 0xfffff000) | ((dev >> 8) & 0xfff));
        uint minor = (uint)(((dev >> 12) & 0xffffff00) | (dev & 0xff));
        return DrmDevice.FromNumbers(major, minor);
    }

    private static VideoSize Size(VideoFormat format) =>
        new(format.CodedSize.Width, format.CodedSize.Height);

    // Where the visible picture starts in a plane, in bytes and rows.
    private static (int X, int Y) PlaneOrigin(PixelFormat format, int plane, VideoRect visible) =>
        format switch
        {
            PixelFormat.Bgra or PixelFormat.Rgba => (visible.X * 4, visible.Y),
            PixelFormat.Nv12 => plane == 0
                ? (visible.X, visible.Y)
                : (visible.X & ~1, visible.Y / 2),
            _ => plane == 0 ? (visible.X, visible.Y) : (visible.X / 2, visible.Y / 2),
        };

    [LoggerMessage(
        2510,
        LogLevel.Information,
        "Publishing PipeWire video node {Name}: {Format} {Width}x{Height}, shared GPU buffers: {Shared}."
    )]
    private partial void LogPublishing(
        string name,
        PixelFormat format,
        int width,
        int height,
        bool shared
    );
}
