using System.Runtime.CompilerServices;

namespace Agash.StreamTransport;

/// <summary>
/// One plane of a Linux DMA-BUF surface: the backing file descriptor and where the plane sits within it.
/// Several planes may share one <see cref="Fd"/> at different offsets (a common dmabuf layout).
/// </summary>
/// <param name="Fd">DMA-BUF file descriptor backing this plane (owned by the producer; valid for the frame's lifetime).</param>
/// <param name="Offset">Byte offset of the plane within <paramref name="Fd"/>.</param>
/// <param name="Stride">Bytes per row of the plane.</param>
public readonly record struct DmaBufPlane(int Fd, uint Offset, uint Stride);

// Inline storage for up to four planes (4:4:4/aux is the practical maximum; NV12 uses 2, packed uses 1).
// Keeps a DmaBufSurface a pure value type with no per-frame heap allocation - the pixels stay on the GPU
// and even the descriptor never touches the managed heap.
[InlineArray(MaxPlanes)]
internal struct DmaBufPlaneArray
{
    public const int MaxPlanes = 4;
    private DmaBufPlane _element0;
}

/// <summary>
/// A Linux DMA-BUF video surface: a DRM format modifier plus the per-plane layout needed to import the
/// frame zero-copy into a GPU (VAAPI via DRM-PRIME, or Vulkan via <c>VK_EXT_image_drm_format_modifier</c>).
/// Carried by a <see cref="VideoFrame"/> whose <see cref="VideoFrame.InteropKind"/> is
/// <see cref="StreamInteropKind.PipeWire"/>. A value type - no allocation per frame.
/// </summary>
public readonly struct DmaBufSurface
{
    private readonly DmaBufPlaneArray _planes;

    /// <summary>Builds a surface from its planes (at most four are retained).</summary>
    public DmaBufSurface(ulong modifier, VideoPixelFormat format, ReadOnlySpan<DmaBufPlane> planes)
    {
        Modifier = modifier;
        Format = format;
        PlaneCount = Math.Min(planes.Length, DmaBufPlaneArray.MaxPlanes);
        for (int i = 0; i < PlaneCount; i++)
        {
            _planes[i] = planes[i];
        }
    }

    /// <summary>The DRM format modifier (tiling/compression layout) shared by the planes.</summary>
    public ulong Modifier { get; }

    /// <summary>The pixel layout of the surface (e.g. <see cref="VideoPixelFormat.Nv12"/>).</summary>
    public VideoPixelFormat Format { get; }

    /// <summary>Number of valid planes (indices <c>0 .. PlaneCount-1</c> via <see cref="this[int]"/>).</summary>
    public int PlaneCount { get; }

    /// <summary>The plane at <paramref name="index"/> (0-based, below <see cref="PlaneCount"/>).</summary>
    // An indexer rather than a Span property: returning a span over the inline-array storage would expose
    // a reference to this struct's fields (CS8170), so we return planes by value instead - still no heap.
    public DmaBufPlane this[int index]
    {
        get
        {
            if ((uint)index >= (uint)PlaneCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _planes[index];
        }
    }
}
