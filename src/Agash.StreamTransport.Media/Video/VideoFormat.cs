using System.Runtime.CompilerServices;

namespace Agash.StreamTransport.Media;

/// <summary>How a frame's pixels are laid out in memory.</summary>
public enum PixelFormat
{
    /// <summary>8-bit 4:2:0: a Y plane, then interleaved U and V at half resolution. What encoders take.</summary>
    Nv12,

    /// <summary>10-bit 4:2:0 in 16-bit containers, laid out as <see cref="Nv12"/>.</summary>
    P010,

    /// <summary>8-bit 4:2:0: Y, U and V planes.</summary>
    I420,

    /// <summary>Packed 8-bit blue, green, red, alpha.</summary>
    Bgra,

    /// <summary>Packed 8-bit red, green, blue, alpha.</summary>
    Rgba,
}

/// <summary>The YUV to RGB matrix (ITU-T H.273 MatrixCoefficients).</summary>
public enum ColorMatrix
{
    /// <summary>Not stated: consumers assume BT.709 for HD and BT.601 below.</summary>
    Unspecified,

    /// <summary>BT.601.</summary>
    Bt601,

    /// <summary>BT.709.</summary>
    Bt709,

    /// <summary>BT.2020 non-constant luminance.</summary>
    Bt2020,

    /// <summary>RGB: no matrix.</summary>
    Identity,
}

/// <summary>Whether sample values span the full range or the video range.</summary>
public enum ColorRange
{
    /// <summary>Not stated: consumers assume limited for YUV and full for RGB.</summary>
    Unspecified,

    /// <summary>Video (studio) range: Y 16-235, chroma 16-240 at 8 bits.</summary>
    Limited,

    /// <summary>Full range: 0-255 at 8 bits.</summary>
    Full,
}

/// <summary>The colour primaries (ITU-T H.273 ColourPrimaries).</summary>
public enum ColorPrimaries
{
    /// <summary>Not stated.</summary>
    Unspecified,

    /// <summary>BT.601 (SMPTE 170M).</summary>
    Bt601,

    /// <summary>BT.709, which sRGB shares.</summary>
    Bt709,

    /// <summary>BT.2020.</summary>
    Bt2020,
}

/// <summary>The transfer characteristic (ITU-T H.273 TransferCharacteristics).</summary>
public enum TransferFunction
{
    /// <summary>Not stated.</summary>
    Unspecified,

    /// <summary>BT.709.</summary>
    Bt709,

    /// <summary>sRGB (IEC 61966-2-1).</summary>
    Srgb,

    /// <summary>SMPTE ST 2084 perceptual quantiser (HDR10).</summary>
    Pq,

    /// <summary>Hybrid log-gamma (ARIB STD-B67).</summary>
    Hlg,

    /// <summary>Linear light.</summary>
    Linear,
}

/// <summary>Where subsampled chroma samples sit relative to luma.</summary>
public enum ChromaSiting
{
    /// <summary>Not stated: consumers assume <see cref="Left"/>.</summary>
    Unspecified,

    /// <summary>Horizontally with the left luma sample, vertically between rows (MPEG-2, H.264 default).</summary>
    Left,

    /// <summary>Between luma samples both ways (JPEG, MPEG-1).</summary>
    Center,

    /// <summary>With the top-left luma sample (BT.2020, HEVC 4:2:2 and 4:4:4 default).</summary>
    TopLeft,
}

/// <summary>How a frame's samples map to colours.</summary>
/// <param name="Matrix">The YUV to RGB matrix.</param>
/// <param name="Range">The sample range.</param>
/// <param name="Primaries">The colour primaries.</param>
/// <param name="Transfer">The transfer characteristic.</param>
/// <param name="ChromaSiting">Where chroma sits.</param>
public readonly record struct VideoColor(
    ColorMatrix Matrix,
    ColorRange Range,
    ColorPrimaries Primaries,
    TransferFunction Transfer,
    ChromaSiting ChromaSiting = ChromaSiting.Unspecified
)
{
    /// <summary>SDR BT.709 in video range, what cameras and screen capture produce for HD.</summary>
    public static VideoColor Bt709 { get; } =
        new(
            ColorMatrix.Bt709,
            ColorRange.Limited,
            ColorPrimaries.Bt709,
            TransferFunction.Bt709,
            ChromaSiting.Left
        );

    /// <summary>sRGB, as desktop RGB content is.</summary>
    public static VideoColor Srgb { get; } =
        new(ColorMatrix.Identity, ColorRange.Full, ColorPrimaries.Bt709, TransferFunction.Srgb);
}

/// <summary>A size in pixels.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct VideoSize(int Width, int Height);

/// <summary>A rectangle in pixels.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct VideoRect(int X, int Y, int Width, int Height)
{
    /// <summary>The whole of a size.</summary>
    /// <param name="size">The size.</param>
    /// <returns>The rectangle from the origin.</returns>
    public static VideoRect Of(VideoSize size) => new(0, 0, size.Width, size.Height);
}

/// <summary>A frame's pixel format and geometry.</summary>
/// <param name="PixelFormat">The pixel layout.</param>
/// <param name="CodedSize">The buffer's size, which may exceed what is shown (alignment padding).</param>
/// <param name="VisibleRect">The part of the buffer that is the picture.</param>
public readonly record struct VideoFormat(
    PixelFormat PixelFormat,
    VideoSize CodedSize,
    VideoRect VisibleRect
)
{
    /// <summary>A format whose whole buffer is visible.</summary>
    /// <param name="pixelFormat">The pixel layout.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    public VideoFormat(PixelFormat pixelFormat, int width, int height)
        : this(pixelFormat, new VideoSize(width, height), new VideoRect(0, 0, width, height)) { }
}

/// <summary>A quarter-turn rotation.</summary>
public enum VideoRotation
{
    /// <summary>Upright.</summary>
    None,

    /// <summary>90 degrees clockwise.</summary>
    Clockwise90,

    /// <summary>180 degrees.</summary>
    Rotate180,

    /// <summary>90 degrees counter-clockwise.</summary>
    CounterClockwise90,
}

/// <summary>How to turn the buffer to show the picture upright: rotation after an optional mirror.</summary>
/// <param name="Rotation">The rotation.</param>
/// <param name="Mirrored">Whether the buffer is mirrored left to right before the rotation.</param>
public readonly record struct VideoOrientation(VideoRotation Rotation, bool Mirrored = false);

/// <summary>Where one plane of a CPU frame is: its offset into the frame's memory and its row pitch.</summary>
/// <param name="Offset">Bytes from the start of the frame's memory to the plane.</param>
/// <param name="Stride">Bytes from one row to the next.</param>
public readonly record struct VideoPlane(int Offset, int Stride);

/// <summary>The planes of a frame, up to four, inline so a layout costs no allocation.</summary>
[InlineArray(MaximumPlanes)]
public struct VideoPlanes
{
    /// <summary>The most planes a frame has.</summary>
    public const int MaximumPlanes = 4;

    private VideoPlane _first;
}

/// <summary>The plane layout of a CPU frame.</summary>
public readonly struct PlaneLayout : IEquatable<PlaneLayout>
{
    private readonly VideoPlanes _planes;

    /// <summary>A layout of the given planes.</summary>
    /// <param name="planes">The planes, at most <see cref="VideoPlanes.MaximumPlanes"/>.</param>
    public PlaneLayout(ReadOnlySpan<VideoPlane> planes)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            planes.Length,
            VideoPlanes.MaximumPlanes,
            nameof(planes)
        );
        Count = planes.Length;
        planes.CopyTo(_planes);
    }

    /// <summary>How many planes there are.</summary>
    public int Count { get; }

    /// <summary>A plane.</summary>
    /// <param name="index">Which.</param>
    /// <returns>The plane.</returns>
    public VideoPlane this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                (uint)index,
                (uint)Count,
                nameof(index)
            );
            return _planes[index];
        }
    }

    /// <summary>The tightly packed layout of a format: planes back to back, rows without padding.</summary>
    /// <param name="format">The pixel format.</param>
    /// <param name="size">The coded size.</param>
    /// <returns>The layout.</returns>
    public static PlaneLayout Packed(PixelFormat format, VideoSize size)
    {
        int w = size.Width;
        int h = size.Height;
        return format switch
        {
            PixelFormat.Nv12 => new PlaneLayout([new(0, w), new(w * h, w)]),
            PixelFormat.P010 => new PlaneLayout([new(0, 2 * w), new(2 * w * h, 2 * w)]),
            PixelFormat.I420 => new PlaneLayout([
                new(0, w),
                new(w * h, w / 2),
                new((w * h) + ((w / 2) * (h / 2)), w / 2),
            ]),
            PixelFormat.Bgra or PixelFormat.Rgba => new PlaneLayout([new(0, 4 * w)]),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };
    }

    /// <summary>The bytes a tightly packed frame of a format takes.</summary>
    /// <param name="format">The pixel format.</param>
    /// <param name="size">The coded size.</param>
    /// <returns>The size in bytes.</returns>
    public static int PackedSize(PixelFormat format, VideoSize size) =>
        format switch
        {
            PixelFormat.Nv12 or PixelFormat.I420 => size.Width * size.Height * 3 / 2,
            PixelFormat.P010 => size.Width * size.Height * 3,
            PixelFormat.Bgra or PixelFormat.Rgba => size.Width * size.Height * 4,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };

    /// <inheritdoc/>
    public bool Equals(PlaneLayout other)
    {
        if (Count != other.Count)
        {
            return false;
        }

        for (int i = 0; i < Count; i++)
        {
            if (_planes[i] != other._planes[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PlaneLayout other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(Count);
        for (int i = 0; i < Count; i++)
        {
            hash.Add(_planes[i]);
        }

        return hash.ToHashCode();
    }

    /// <summary>Whether two layouts are the same.</summary>
    /// <param name="left">One layout.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they are equal.</returns>
    public static bool operator ==(PlaneLayout left, PlaneLayout right) => left.Equals(right);

    /// <summary>Whether two layouts differ.</summary>
    /// <param name="left">One layout.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(PlaneLayout left, PlaneLayout right) => !left.Equals(right);
}

/// <summary>How a <see cref="GpuIdentity"/> names its GPU.</summary>
public enum GpuIdentityKind
{
    /// <summary>A DXGI adapter's locally unique identifier (Windows).</summary>
    DxgiAdapterLuid,

    /// <summary>A DRM device's <c>dev_t</c> (Linux).</summary>
    DrmDevice,

    /// <summary>A Metal device's registry ID (macOS).</summary>
    MetalRegistryId,
}

/// <summary>
/// Which GPU a surface lives on. A surface is only usable on its own GPU, and pipelines are checked
/// against this before the first frame.
/// </summary>
/// <param name="Kind">How the GPU is named.</param>
/// <param name="Value">The identifier.</param>
public readonly record struct GpuIdentity(GpuIdentityKind Kind, ulong Value)
{
    /// <summary>A DXGI adapter.</summary>
    /// <param name="luid">The adapter LUID, low part in the low 32 bits.</param>
    /// <returns>The identity.</returns>
    public static GpuIdentity FromAdapterLuid(ulong luid) =>
        new(GpuIdentityKind.DxgiAdapterLuid, luid);

    /// <summary>A DRM device.</summary>
    /// <param name="deviceNumber">Its <c>dev_t</c>.</param>
    /// <returns>The identity.</returns>
    public static GpuIdentity FromDrmDevice(ulong deviceNumber) =>
        new(GpuIdentityKind.DrmDevice, deviceNumber);

    /// <summary>A Metal device.</summary>
    /// <param name="registryId">Its <c>registryID</c>.</param>
    /// <returns>The identity.</returns>
    public static GpuIdentity FromMetalRegistryId(ulong registryId) =>
        new(GpuIdentityKind.MetalRegistryId, registryId);
}
