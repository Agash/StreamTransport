using Agash.StreamTransport.Linux.Vulkan;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PipeWire.NET;
using PipeWire.NET.Media;
using Vortice.Vulkan;
using PipeWireFormat = PipeWire.NET.Media.PixelFormat;
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
/// camera. GPU frames stay on the GPU: DMA-BUF frames (BGRA, RGBA or NV12) are published on buffers
/// shared with the consumer, each filled with one copy on the GPU from the latest frame; a consumer
/// that cannot import a shared buffer is given the frames read back into memory. Frames in
/// memory (BGRA, RGBA, NV12 or I420) are copied once into a staging buffer the daemon's next buffer is
/// filled from. The node asks for a cycle per frame, so frames go out as they arrive; it takes the
/// size, format, colour and storage of the first frame, declares the colour so consumers convert it
/// correctly, and is made again when any of them change.
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
    private VideoFrameLease? _latest;
    private byte[] _staging = [];
    private bool _staged;
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
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            VideoFormat format = new(
                pixels,
                frame.Format.VisibleRect.Width,
                frame.Format.VisibleRect.Height
            );
            if (_output is null || format != _format || frame.Color != _color || gpu != _gpu)
            {
                Restart(format, frame.Color, gpu, frame.Storage.Device);
            }

            if (gpu)
            {
                // Kept until a newer frame arrives: the consumer's buffers are filled from it.
                _latest?.Dispose();
                _latest = frame.Retain();
            }
            else
            {
                Stage(in frame);
            }

            output = _output!;
        }

        output.TriggerProcess();
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
            _latest?.Dispose();
            _latest = null;
            DropShared();
        }
    }

    // A node of the frame's size, format, colour and storage; called holding the lock.
    private void Restart(VideoFormat format, VideoColor color, bool gpu, GpuIdentity? device)
    {
        _output?.Dispose();
        _output = null;
        _latest?.Dispose();
        _latest = null;
        DropShared();
        _format = format;
        _color = color;
        _gpu = gpu;
        _staged = false;
        PipeWireVideoOutput output = new(
            _context,
            _name,
            format.CodedSize.Width,
            format.CodedSize.Height,
            PipeWireMapping.ToPipeWire(format.PixelFormat)!.Value,
            _options.FrameRate,
            PipeWireMapping.ToPipeWire(color)
        );
        try
        {
            if (gpu)
            {
                _engine = VulkanEngine.For(device);
                output.AllocateDmaBuf += Allocate;
                output.FillDmaBuf += FillShared;
                output.ReleaseDmaBuf += ReleaseShared;

                // A consumer that cannot import a shared buffer gets frames read back into memory.
                output.HostMemoryFallback = true;
                output.FillFrame += FillFromGpu;
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
                _staging = new byte[PlaneLayout.PackedSize(format.PixelFormat, Size(format))];
                output.FillFrame += Fill;
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
            VulkanImage[] images = _format.PixelFormat switch
            {
                PixelFormat.Nv12 =>
                [
                    VulkanImage.Export(engine, VkFormat.R8Unorm, width, height, usage),
                    VulkanImage.Export(engine, VkFormat.R8G8Unorm, width / 2, height / 2, usage),
                ],
                PixelFormat.Rgba =>
                [
                    VulkanImage.Export(engine, VkFormat.R8G8B8A8Unorm, width, height, usage),
                ],
                _ => [VulkanImage.Export(engine, VkFormat.B8G8R8A8Unorm, width, height, usage)],
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

    // Runs on the PipeWire loop thread: the latest frame into the buffer the consumer takes next.
    private bool FillShared(PipeWireVideoOutput sender, int bufferIndex)
    {
        lock (_gate)
        {
            if (
                sender != _output
                || _latest is null
                || !_shared.TryGetValue(bufferIndex, out VulkanImage[]? images)
            )
            {
                return false;
            }

            VideoFrame latest = _latest.Frame;
            _ = latest.Storage.TryGetValue(out DmaBufImage image);
            VulkanTransfer.Copy(
                _engine!,
                in image,
                _format.PixelFormat,
                latest.Format.CodedSize.Width,
                latest.Format.CodedSize.Height,
                images
            );
            return true;
        }
    }

    // Runs on the PipeWire loop thread when the consumer took memory: the latest GPU frame, read back
    // into the daemon's buffer, planes one after another at its stride.
    private bool FillFromGpu(
        PipeWireVideoOutput sender,
        Span<byte> pixels,
        int stride,
        int width,
        int height,
        PipeWireFormat format
    )
    {
        lock (_gate)
        {
            if (sender != _output || _latest is null)
            {
                return false;
            }

            VideoFrame latest = _latest.Frame;
            _ = latest.Storage.TryGetValue(out DmaBufImage image);
            VideoSize size = Size(_format);
            byte[][] planes = VulkanTransfer.Read(
                _engine!,
                in image,
                _format.PixelFormat,
                size.Width,
                size.Height
            );
            var packed = PlaneLayout.Packed(_format.PixelFormat, size);
            int offset = 0;
            for (int plane = 0; plane < planes.Length; plane++)
            {
                int planeStride = stride * packed[plane].Stride / packed[0].Stride;
                int rowBytes = packed[plane].Stride;
                int rows = PlaneLayout.PlaneRows(_format.PixelFormat, plane, size.Height);
                for (int row = 0; row < rows; row++)
                {
                    planes[plane]
                        .AsSpan(row * rowBytes, rowBytes)
                        .CopyTo(pixels[(offset + (row * planeStride))..]);
                }

                offset += rows * planeStride;
            }

            return true;
        }
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

    // The visible picture, rows packed tightly, into the staging buffer; called holding the lock.
    private void Stage(in VideoFrame frame)
    {
        VideoSize size = Size(_format);
        var packed = PlaneLayout.Packed(_format.PixelFormat, size);
        _ = frame.Storage.TryGetValue(out CpuImage image);
        VideoRect visible = frame.Format.VisibleRect;
        for (int plane = 0; plane < packed.Count; plane++)
        {
            (int x, int y) = PlaneOrigin(_format.PixelFormat, plane, visible);
            int sourceStride = image.Planes[plane].Stride;
            ReadOnlySpan<byte> source = frame.GetPlane(plane);
            int rowBytes = packed[plane].Stride;
            int rows = PlaneLayout.PlaneRows(_format.PixelFormat, plane, size.Height);
            for (int row = 0; row < rows; row++)
            {
                source
                    .Slice(((y + row) * sourceStride) + x, rowBytes)
                    .CopyTo(_staging.AsSpan(packed[plane].Offset + (row * rowBytes)));
            }
        }

        _staged = true;
    }

    // Runs on the PipeWire loop thread: the staged frame into the daemon's buffer, at its stride.
    private bool Fill(
        PipeWireVideoOutput sender,
        Span<byte> pixels,
        int stride,
        int width,
        int height,
        PipeWireFormat format
    )
    {
        lock (_gate)
        {
            if (!_staged || sender != _output)
            {
                return false;
            }

            VideoSize size = Size(_format);
            var packed = PlaneLayout.Packed(_format.PixelFormat, size);
            int offset = 0;
            for (int plane = 0; plane < packed.Count; plane++)
            {
                // The daemon's planes follow each other, each at the first plane's stride scaled as
                // the plane's rows are to the first's.
                int planeStride = stride * packed[plane].Stride / packed[0].Stride;
                int rowBytes = packed[plane].Stride;
                int rows = PlaneLayout.PlaneRows(_format.PixelFormat, plane, size.Height);
                for (int row = 0; row < rows; row++)
                {
                    _staging
                        .AsSpan(packed[plane].Offset + (row * rowBytes), rowBytes)
                        .CopyTo(pixels[(offset + (row * planeStride))..]);
                }

                offset += rows * planeStride;
            }

            return true;
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
