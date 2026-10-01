using Agash.StreamTransport.Media;
using Vortice.Vulkan;

namespace Agash.StreamTransport.Linux.Vulkan;

/// <summary>DRM fourccs of the pictures this package makes and reads.</summary>
internal static class DrmFourcc
{
    /// <summary>NV12: a luma plane and an interleaved CbCr plane.</summary>
    public const uint Nv12 = 0x3231564E;

    /// <summary>ARGB8888, which is B, G, R, A in memory: <see cref="PixelFormat.Bgra"/>.</summary>
    public const uint Argb8888 = 0x34325241;

    /// <summary>ABGR8888, which is R, G, B, A in memory: <see cref="PixelFormat.Rgba"/>.</summary>
    public const uint Abgr8888 = 0x34324241;

    /// <summary>XRGB8888, BGRA without alpha.</summary>
    public const uint Xrgb8888 = 0x34325258;

    /// <summary>XBGR8888, RGBA without alpha.</summary>
    public const uint Xbgr8888 = 0x34324258;

    public static uint Of(PixelFormat format) =>
        format switch
        {
            PixelFormat.Nv12 => Nv12,
            PixelFormat.Bgra => Argb8888,
            PixelFormat.Rgba => Abgr8888,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };

    public static PixelFormat? ToPixelFormat(uint fourcc) =>
        fourcc switch
        {
            Nv12 => PixelFormat.Nv12,
            Argb8888 or Xrgb8888 => PixelFormat.Bgra,
            Abgr8888 or Xbgr8888 => PixelFormat.Rgba,
            _ => null,
        };
}

/// <summary>
/// Pictures of one format and size on exported DMA-BUFs, reused once nothing holds them: NV12 as a
/// luma and a chroma image, BGRA or RGBA as one.
/// </summary>
internal sealed class DmaBufPool : IDisposable
{
    private readonly VulkanEngine _engine;
    private readonly Stack<PooledDmaBuf> _free = new();
    private readonly List<PooledDmaBuf> _all = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    public DmaBufPool(VulkanEngine engine, PixelFormat format, VideoSize size)
    {
        _engine = engine;
        Format = format;
        Size = size;
    }

    public PixelFormat Format { get; }

    public VideoSize Size { get; }

    public bool Makes(PixelFormat format, VideoSize size) => Format == format && Size == size;

    /// <summary>A picture, held once; release it when done.</summary>
    public PooledDmaBuf Rent()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_free.TryPop(out PooledDmaBuf? picture))
            {
                picture = new PooledDmaBuf(this, Create());
                _all.Add(picture);
            }

            picture.Held();
            return picture;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (PooledDmaBuf picture in _free)
            {
                picture.Destroy();
            }

            _free.Clear();
        }
    }

    internal void Return(PooledDmaBuf picture)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                picture.Destroy();
            }
            else
            {
                _free.Push(picture);
            }
        }
    }

    private VulkanImage[] Create()
    {
        const VkImageUsageFlags usage =
            VkImageUsageFlags.Storage
            | VkImageUsageFlags.Sampled
            | VkImageUsageFlags.TransferSrc
            | VkImageUsageFlags.TransferDst;
        return Format switch
        {
            PixelFormat.Nv12 => VulkanImage.ExportPicture(
                _engine,
                [
                    (VkFormat.R8Unorm, Size.Width, Size.Height),
                    (VkFormat.R8G8Unorm, Size.Width / 2, Size.Height / 2),
                ],
                usage
            ),
            PixelFormat.Bgra => VulkanImage.ExportPicture(
                _engine,
                [(VkFormat.B8G8R8A8Unorm, Size.Width, Size.Height)],
                usage
            ),
            PixelFormat.Rgba => VulkanImage.ExportPicture(
                _engine,
                [(VkFormat.R8G8B8A8Unorm, Size.Width, Size.Height)],
                usage
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(Format), Format, null),
        };
    }
}

/// <summary>
/// A pooled picture, counted: the producer holds it while it delivers the frame, and each kept frame
/// holds it again. It returns to its pool when the last holder releases it.
/// </summary>
internal sealed class PooledDmaBuf(DmaBufPool pool, VulkanImage[] planes)
{
    private int _holds;

    public VulkanImage[] Planes { get; } = planes;

    public void Hold() => Interlocked.Increment(ref _holds);

    public void Release()
    {
        if (Interlocked.Decrement(ref _holds) == 0)
        {
            pool.Return(this);
        }
    }

    /// <summary>The picture as DMA-BUF storage on its GPU.</summary>
    public DmaBufImage Describe(GpuIdentity device)
    {
        Span<DmaBufPlane> planes = stackalloc DmaBufPlane[Planes.Length];
        for (int i = 0; i < planes.Length; i++)
        {
            planes[i] = Planes[i].Plane;
        }

        return new DmaBufImage(
            planes,
            DrmFourcc.Of(pool.Format),
            VulkanEngine.LinearModifier,
            device
        );
    }

    internal void Held() => _holds = 1;

    internal void Destroy()
    {
        foreach (VulkanImage plane in Planes)
        {
            plane.Dispose();
        }
    }
}

/// <summary>A frame on a pooled picture, kept by a consumer; the picture returns to its pool on release.</summary>
internal sealed class DmaBufFrameLease : VideoFrameLease, IVideoFrameRetainer
{
    private readonly PooledDmaBuf _picture;

    public DmaBufFrameLease(in VideoFrame frame, PooledDmaBuf picture)
        : base(
            frame.Storage,
            frame.Format,
            frame.Timestamp,
            frame.Color,
            frame.Orientation,
            frame.Duration
        )
    {
        _picture = picture;
    }

    protected override IVideoFrameRetainer KeepAgain => this;

    public VideoFrameLease Retain(in VideoFrame frame)
    {
        _picture.Hold();
        return new DmaBufFrameLease(in frame, _picture);
    }

    protected override void Release() => _picture.Release();
}
