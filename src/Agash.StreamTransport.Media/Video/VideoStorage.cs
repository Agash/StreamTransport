using System.Runtime.CompilerServices;

namespace Agash.StreamTransport.Media;

/// <summary>Where a frame's pixels live; a closed set, matched by <see cref="VideoStorage"/>.</summary>
public enum VideoStorageKind
{
    /// <summary>CPU memory: <see cref="CpuImage"/>.</summary>
    Cpu,

    /// <summary>A Direct3D 12 resource: <see cref="D3D12Image"/>.</summary>
    D3D12,

    /// <summary>A Direct3D 11 texture: <see cref="D3D11Image"/>.</summary>
    D3D11,

    /// <summary>An IOSurface: <see cref="IOSurfaceImage"/>.</summary>
    IOSurface,

    /// <summary>A Linux DMA-BUF: <see cref="DmaBufImage"/>.</summary>
    DmaBuf,
}

/// <summary>
/// Pixels in CPU memory. The bytes themselves are <see cref="VideoFrame.CpuData"/>, which a borrowed
/// frame only holds for the call it is passed to; this describes their layout.
/// </summary>
/// <param name="Planes">Where each plane is in <see cref="VideoFrame.CpuData"/>.</param>
public readonly record struct CpuImage(PlaneLayout Planes);

/// <summary>
/// When a Direct3D 12 resource is ready: after the work already submitted to
/// <paramref name="ProducerQueue"/>, or when <paramref name="Fence"/> reaches <paramref name="Value"/>.
/// Both zero means it is ready when the frame is delivered.
/// </summary>
/// <param name="ProducerQueue">The ID3D12CommandQueue the resource was written on, or zero.</param>
/// <param name="Fence">An ID3D12Fence to wait on, or zero.</param>
/// <param name="Value">The value <paramref name="Fence"/> reaches when the resource is ready.</param>
public readonly record struct D3D12Sync(nint ProducerQueue = 0, nint Fence = 0, ulong Value = 0);

/// <summary>A Direct3D 12 texture (Windows): one 2D resource with one mip level and array slice.</summary>
/// <param name="Resource">The ID3D12Resource, borrowed, in D3D12_RESOURCE_STATE_COMMON.</param>
/// <param name="Adapter">The adapter the resource lives on.</param>
/// <param name="Sync">How to wait for the producer.</param>
public readonly record struct D3D12Image(
    nint Resource,
    GpuIdentity Adapter,
    D3D12Sync Sync = default
);

/// <summary>How the producer and consumer of a Direct3D 11 texture take turns with it.</summary>
public enum D3D11SyncKind
{
    /// <summary>None: the producer's work is finished when the frame is delivered.</summary>
    None,

    /// <summary>An IDXGIKeyedMutex: acquire with <see cref="D3D11Sync.Value"/> before reading.</summary>
    KeyedMutex,

    /// <summary>An ID3D11Fence: wait for it to reach <see cref="D3D11Sync.Value"/> before reading.</summary>
    Fence,
}

/// <summary>Synchronisation for a Direct3D 11 texture.</summary>
/// <param name="Kind">The primitive.</param>
/// <param name="Fence">The ID3D11Fence, for <see cref="D3D11SyncKind.Fence"/>.</param>
/// <param name="Value">The keyed mutex key or the fence value.</param>
public readonly record struct D3D11Sync(D3D11SyncKind Kind, nint Fence = 0, ulong Value = 0);

/// <summary>A Direct3D 11 texture (Windows).</summary>
/// <param name="Texture">The ID3D11Texture2D, borrowed.</param>
/// <param name="Subresource">The array slice or subresource the frame is in.</param>
/// <param name="Adapter">The adapter the texture lives on.</param>
/// <param name="Sync">How to wait for the producer.</param>
public readonly record struct D3D11Image(
    nint Texture,
    int Subresource,
    GpuIdentity Adapter,
    D3D11Sync Sync = default
);

/// <summary>An IOSurface (macOS), shared with Metal, Core Video and VideoToolbox.</summary>
/// <param name="Surface">The IOSurfaceRef, borrowed.</param>
/// <param name="Device">The GPU the surface was last written on.</param>
/// <param name="SharedEvent">An MTLSharedEvent to wait on before reading, or zero.</param>
/// <param name="SignalValue">The value <paramref name="SharedEvent"/> reaches when the producer is done.</param>
public readonly record struct IOSurfaceImage(
    nint Surface,
    GpuIdentity Device,
    nint SharedEvent = 0,
    ulong SignalValue = 0
);

/// <summary>One plane of a DMA-BUF.</summary>
/// <param name="Fd">The file descriptor, borrowed.</param>
/// <param name="Offset">Where the plane starts in the buffer.</param>
/// <param name="Stride">Bytes per row.</param>
public readonly record struct DmaBufPlane(int Fd, int Offset, int Stride);

/// <summary>The planes of a DMA-BUF, inline.</summary>
[InlineArray(VideoPlanes.MaximumPlanes)]
public struct DmaBufPlanes
{
    private DmaBufPlane _first;
}

/// <summary>
/// Explicit synchronisation for a DMA-BUF (a DRM syncobj timeline, as PipeWire's SyncTimeline meta
/// carries): wait for the acquire point before reading, signal the release point when done.
/// </summary>
/// <param name="AcquireSyncobj">The syncobj's file descriptor for the acquire point.</param>
/// <param name="AcquirePoint">The timeline point the producer signals when the buffer is ready.</param>
/// <param name="ReleaseSyncobj">The syncobj's file descriptor for the release point.</param>
/// <param name="ReleasePoint">The timeline point the consumer signals when it is done.</param>
public readonly record struct DrmSyncTimeline(
    int AcquireSyncobj,
    ulong AcquirePoint,
    int ReleaseSyncobj,
    ulong ReleasePoint
);

/// <summary>A Linux DMA-BUF, whatever made it: PipeWire, V4L2, a DRM plane or a capture block.</summary>
public readonly struct DmaBufImage : IEquatable<DmaBufImage>
{
    private readonly DmaBufPlanes _planes;

    /// <summary>A DMA-BUF.</summary>
    /// <param name="planes">Its planes, at most four.</param>
    /// <param name="drmFormat">The DRM fourcc (<c>DRM_FORMAT_NV12</c> and so on).</param>
    /// <param name="modifier">The DRM format modifier.</param>
    /// <param name="device">The GPU it was allocated on.</param>
    /// <param name="sync">Explicit synchronisation, when the producer uses it.</param>
    public DmaBufImage(
        ReadOnlySpan<DmaBufPlane> planes,
        uint drmFormat,
        ulong modifier,
        GpuIdentity device,
        DrmSyncTimeline? sync = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            planes.Length,
            VideoPlanes.MaximumPlanes,
            nameof(planes)
        );
        planes.CopyTo(_planes);
        PlaneCount = planes.Length;
        DrmFormat = drmFormat;
        Modifier = modifier;
        Device = device;
        Sync = sync;
    }

    /// <summary>How many planes there are.</summary>
    public int PlaneCount { get; }

    /// <summary>The DRM fourcc.</summary>
    public uint DrmFormat { get; }

    /// <summary>The DRM format modifier.</summary>
    public ulong Modifier { get; }

    /// <summary>The GPU it was allocated on.</summary>
    public GpuIdentity Device { get; }

    /// <summary>Explicit synchronisation, when the producer uses it.</summary>
    public DrmSyncTimeline? Sync { get; }

    /// <summary>A plane.</summary>
    /// <param name="index">Which.</param>
    /// <returns>The plane.</returns>
    public DmaBufPlane this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                (uint)index,
                (uint)PlaneCount,
                nameof(index)
            );
            return _planes[index];
        }
    }

    /// <inheritdoc/>
    public bool Equals(DmaBufImage other)
    {
        if (
            PlaneCount != other.PlaneCount
            || DrmFormat != other.DrmFormat
            || Modifier != other.Modifier
            || Device != other.Device
            || Sync != other.Sync
        )
        {
            return false;
        }

        for (int i = 0; i < PlaneCount; i++)
        {
            if (_planes[i] != other._planes[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DmaBufImage other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(
            PlaneCount,
            DrmFormat,
            Modifier,
            Device,
            Sync,
            PlaneCount > 0 ? _planes[0] : default
        );

    /// <summary>Whether two images are the same.</summary>
    /// <param name="left">One image.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they are equal.</returns>
    public static bool operator ==(DmaBufImage left, DmaBufImage right) => left.Equals(right);

    /// <summary>Whether two images differ.</summary>
    /// <param name="left">One image.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(DmaBufImage left, DmaBufImage right) => !left.Equals(right);
}

/// <summary>
/// Where a frame's pixels are: one of <see cref="CpuImage"/>, <see cref="D3D12Image"/>, <see cref="D3D11Image"/>,
/// <see cref="IOSurfaceImage"/> or <see cref="DmaBufImage"/>. A closed union: every <c>switch</c> over
/// it must handle each case, so adding a storage breaks the build wherever one is missed. Matching
/// does not box.
/// </summary>
[Union]
public readonly struct VideoStorage : IUnion, IEquatable<VideoStorage>
{
    private readonly CpuImage _cpu;
    private readonly D3D12Image _d3d12;
    private readonly D3D11Image _d3d11;
    private readonly IOSurfaceImage _ioSurface;
    private readonly DmaBufImage _dmaBuf;

    /// <summary>CPU storage.</summary>
    /// <param name="image">The layout.</param>
    public VideoStorage(CpuImage image)
    {
        Kind = VideoStorageKind.Cpu;
        _cpu = image;
    }

    /// <summary>Direct3D 12 storage.</summary>
    /// <param name="image">The resource.</param>
    public VideoStorage(D3D12Image image)
    {
        Kind = VideoStorageKind.D3D12;
        _d3d12 = image;
    }

    /// <summary>Direct3D 11 storage.</summary>
    /// <param name="image">The texture.</param>
    public VideoStorage(D3D11Image image)
    {
        Kind = VideoStorageKind.D3D11;
        _d3d11 = image;
    }

    /// <summary>IOSurface storage.</summary>
    /// <param name="image">The surface.</param>
    public VideoStorage(IOSurfaceImage image)
    {
        Kind = VideoStorageKind.IOSurface;
        _ioSurface = image;
    }

    /// <summary>DMA-BUF storage.</summary>
    /// <param name="image">The buffer.</param>
    public VideoStorage(DmaBufImage image)
    {
        Kind = VideoStorageKind.DmaBuf;
        _dmaBuf = image;
    }

    /// <summary>Which case this is.</summary>
    public VideoStorageKind Kind { get; }

    /// <summary>The GPU the pixels are on; null for CPU memory.</summary>
    public GpuIdentity? Device =>
        Kind switch
        {
            VideoStorageKind.D3D12 => _d3d12.Adapter,
            VideoStorageKind.D3D11 => _d3d11.Adapter,
            VideoStorageKind.IOSurface => _ioSurface.Device,
            VideoStorageKind.DmaBuf => _dmaBuf.Device,
            _ => null,
        };

    /// <summary>The case as an object, which boxes it; a <c>switch</c> over the storage does not.</summary>
    public object? Value =>
        Kind switch
        {
            VideoStorageKind.Cpu => _cpu,
            VideoStorageKind.D3D12 => _d3d12,
            VideoStorageKind.D3D11 => _d3d11,
            VideoStorageKind.IOSurface => _ioSurface,
            _ => _dmaBuf,
        };

    /// <summary>The CPU case.</summary>
    /// <param name="value">The image, when this is one.</param>
    /// <returns>Whether it is.</returns>
    public bool TryGetValue(out CpuImage value)
    {
        value = _cpu;
        return Kind == VideoStorageKind.Cpu;
    }

    /// <summary>The Direct3D 12 resource, when that is the storage.</summary>
    /// <param name="value">The resource.</param>
    /// <returns>Whether the storage is Direct3D 12.</returns>
    public bool TryGetValue(out D3D12Image value)
    {
        value = _d3d12;
        return Kind == VideoStorageKind.D3D12;
    }

    /// <summary>The Direct3D 11 case.</summary>
    /// <param name="value">The image, when this is one.</param>
    /// <returns>Whether it is.</returns>
    public bool TryGetValue(out D3D11Image value)
    {
        value = _d3d11;
        return Kind == VideoStorageKind.D3D11;
    }

    /// <summary>The IOSurface case.</summary>
    /// <param name="value">The image, when this is one.</param>
    /// <returns>Whether it is.</returns>
    public bool TryGetValue(out IOSurfaceImage value)
    {
        value = _ioSurface;
        return Kind == VideoStorageKind.IOSurface;
    }

    /// <summary>The DMA-BUF case.</summary>
    /// <param name="value">The image, when this is one.</param>
    /// <returns>Whether it is.</returns>
    public bool TryGetValue(out DmaBufImage value)
    {
        value = _dmaBuf;
        return Kind == VideoStorageKind.DmaBuf;
    }

    /// <summary>CPU storage.</summary>
    /// <param name="image">The layout.</param>
    public static implicit operator VideoStorage(CpuImage image) => new(image);

    /// <summary>Direct3D 12 storage.</summary>
    /// <param name="image">The resource.</param>
    public static implicit operator VideoStorage(D3D12Image image) => new(image);

    /// <summary>Direct3D 11 storage.</summary>
    /// <param name="image">The texture.</param>
    public static implicit operator VideoStorage(D3D11Image image) => new(image);

    /// <summary>IOSurface storage.</summary>
    /// <param name="image">The surface.</param>
    public static implicit operator VideoStorage(IOSurfaceImage image) => new(image);

    /// <summary>DMA-BUF storage.</summary>
    /// <param name="image">The buffer.</param>
    public static implicit operator VideoStorage(DmaBufImage image) => new(image);

    /// <inheritdoc/>
    public bool Equals(VideoStorage other) =>
        Kind == other.Kind
        && Kind switch
        {
            VideoStorageKind.Cpu => _cpu == other._cpu,
            VideoStorageKind.D3D12 => _d3d12 == other._d3d12,
            VideoStorageKind.D3D11 => _d3d11 == other._d3d11,
            VideoStorageKind.IOSurface => _ioSurface == other._ioSurface,
            _ => _dmaBuf == other._dmaBuf,
        };

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is VideoStorage other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        Kind switch
        {
            VideoStorageKind.Cpu => _cpu.GetHashCode(),
            VideoStorageKind.D3D12 => _d3d12.GetHashCode(),
            VideoStorageKind.D3D11 => _d3d11.GetHashCode(),
            VideoStorageKind.IOSurface => _ioSurface.GetHashCode(),
            _ => _dmaBuf.GetHashCode(),
        };

    /// <summary>Whether two storages are the same.</summary>
    /// <param name="left">One storage.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they are equal.</returns>
    public static bool operator ==(VideoStorage left, VideoStorage right) => left.Equals(right);

    /// <summary>Whether two storages differ.</summary>
    /// <param name="left">One storage.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(VideoStorage left, VideoStorage right) => !left.Equals(right);
}
