using System.Buffers;
using System.Collections.Immutable;

namespace Agash.StreamTransport.Media;

/// <summary>
/// A video frame, borrowed: valid only for the call it is passed to. Being a <c>ref struct</c>, it cannot
/// be stored in a field, captured by a lambda or carried across an <c>await</c>. A consumer that needs
/// the frame later calls <see cref="Retain"/>, which returns an owning <see cref="VideoFrameLease"/>.
/// </summary>
public readonly ref struct VideoFrame
{
    private readonly ReadOnlySpan<byte> _plane0;
    private readonly ReadOnlySpan<byte> _plane1;
    private readonly ReadOnlySpan<byte> _plane2;
    private readonly ReadOnlySpan<byte> _plane3;
    private readonly IVideoFrameRetainer? _retainer;

    /// <summary>A frame in GPU memory, or in CPU memory as one buffer laid out as its storage says.</summary>
    /// <param name="storage">Where the pixels are.</param>
    /// <param name="format">The pixel format and geometry.</param>
    /// <param name="timestamp">When the frame was made.</param>
    /// <param name="cpuData">
    /// The pixels, for <see cref="VideoStorageKind.Cpu"/> storage: every plane of the
    /// <see cref="CpuImage.Planes"/> layout, at its offset.
    /// </param>
    /// <param name="color">How samples map to colours.</param>
    /// <param name="orientation">How to turn the buffer upright.</param>
    /// <param name="duration">How long the frame is shown; zero when unknown.</param>
    /// <param name="retainer">
    /// How to keep the frame past the call; null when the producer offers nothing, in which case CPU
    /// frames are copied and GPU frames cannot be retained.
    /// </param>
    public VideoFrame(
        VideoStorage storage,
        VideoFormat format,
        MediaTimestamp timestamp,
        ReadOnlySpan<byte> cpuData = default,
        VideoColor color = default,
        VideoOrientation orientation = default,
        TimeSpan duration = default,
        IVideoFrameRetainer? retainer = null
    )
    {
        if (storage.TryGetValue(out CpuImage image))
        {
            if (cpuData.IsEmpty)
            {
                throw new ArgumentException("A CPU frame needs its pixels.", nameof(cpuData));
            }

            PlaneLayout layout = image.Planes;
            _plane0 = Slice(cpuData, format, layout, 0);
            _plane1 = Slice(cpuData, format, layout, 1);
            _plane2 = Slice(cpuData, format, layout, 2);
            _plane3 = Slice(cpuData, format, layout, 3);
            Storage = new CpuImage(Unpacked(layout));
        }
        else
        {
            Storage = storage;
        }

        Format = format;
        Timestamp = timestamp;
        Color = color;
        Orientation = orientation;
        Duration = duration;
        _retainer = retainer;
    }

    /// <summary>
    /// A frame in CPU memory whose planes are separate buffers, as decoders produce them. Pass the planes
    /// the format has (<see cref="PlaneLayout.PlaneCount"/>) and leave the rest empty.
    /// </summary>
    /// <param name="format">The pixel format and geometry.</param>
    /// <param name="timestamp">When the frame was made.</param>
    /// <param name="plane0">The first plane: luma, or the packed pixels.</param>
    /// <param name="stride0">Bytes from one row of <paramref name="plane0"/> to the next.</param>
    /// <param name="plane1">The second plane, if the format has one.</param>
    /// <param name="stride1">Its row pitch.</param>
    /// <param name="plane2">The third plane, if the format has one.</param>
    /// <param name="stride2">Its row pitch.</param>
    /// <param name="plane3">The fourth plane, alpha, if the format has one.</param>
    /// <param name="stride3">Its row pitch.</param>
    /// <param name="color">How samples map to colours.</param>
    /// <param name="orientation">How to turn the buffer upright.</param>
    /// <param name="duration">How long the frame is shown; zero when unknown.</param>
    /// <param name="retainer">How to keep the frame past the call; null to copy it.</param>
    public VideoFrame(
        VideoFormat format,
        MediaTimestamp timestamp,
        ReadOnlySpan<byte> plane0,
        int stride0,
        ReadOnlySpan<byte> plane1 = default,
        int stride1 = 0,
        ReadOnlySpan<byte> plane2 = default,
        int stride2 = 0,
        ReadOnlySpan<byte> plane3 = default,
        int stride3 = 0,
        VideoColor color = default,
        VideoOrientation orientation = default,
        TimeSpan duration = default,
        IVideoFrameRetainer? retainer = null
    )
    {
        int count = PlaneLayout.PlaneCount(format.PixelFormat);
        if (
            plane0.IsEmpty
            || (count > 1 && plane1.IsEmpty)
            || (count > 2 && plane2.IsEmpty)
            || (count > 3 && plane3.IsEmpty)
        )
        {
            throw new ArgumentException(
                $"A {format.PixelFormat} frame has {count} planes and needs each of them."
            );
        }

        ReadOnlySpan<VideoPlane> strides =
        [
            new(0, stride0),
            new(0, stride1),
            new(0, stride2),
            new(0, stride3),
        ];
        _plane0 = plane0;
        _plane1 = count > 1 ? plane1 : default;
        _plane2 = count > 2 ? plane2 : default;
        _plane3 = count > 3 ? plane3 : default;
        Storage = new CpuImage(new PlaneLayout(strides[..count]));
        Format = format;
        Timestamp = timestamp;
        Color = color;
        Orientation = orientation;
        Duration = duration;
        _retainer = retainer;
    }

    /// <summary>
    /// Where the pixels are. A CPU frame's layout has zero offsets: <see cref="GetPlane"/> returns each
    /// plane from its first row.
    /// </summary>
    public VideoStorage Storage { get; }

    /// <summary>The pixel format and geometry.</summary>
    public VideoFormat Format { get; }

    /// <summary>When the frame was made.</summary>
    public MediaTimestamp Timestamp { get; }

    /// <summary>How samples map to colours.</summary>
    public VideoColor Color { get; }

    /// <summary>How to turn the buffer upright.</summary>
    public VideoOrientation Orientation { get; }

    /// <summary>How long the frame is shown; zero when unknown.</summary>
    public TimeSpan Duration { get; }

    /// <summary>The planes of a CPU frame; zero for a GPU frame.</summary>
    public int PlaneCount => Storage.TryGetValue(out CpuImage image) ? image.Planes.Count : 0;

    /// <summary>A plane of a CPU frame, from its first row; its row pitch is the storage layout's stride.</summary>
    /// <param name="index">Which plane.</param>
    /// <returns>The plane's bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The frame has no such plane.</exception>
    public ReadOnlySpan<byte> GetPlane(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            (uint)index,
            (uint)PlaneCount,
            nameof(index)
        );
        return index switch
        {
            0 => _plane0,
            1 => _plane1,
            2 => _plane2,
            _ => _plane3,
        };
    }

    /// <summary>
    /// Keeps the frame past the call it was passed to. Only the producer knows what that costs: a CPU
    /// pool rents, a GPU source copies into an owned surface or holds its buffer. Without a producer
    /// retainer a CPU frame is copied into pooled memory.
    /// </summary>
    /// <returns>A lease, released exactly once by disposing it.</returns>
    /// <exception cref="NotSupportedException">A GPU frame whose producer cannot keep it.</exception>
    public VideoFrameLease Retain() =>
        _retainer is not null ? _retainer.Retain(in this)
        : Storage.Kind == VideoStorageKind.Cpu ? CpuVideoFrameLease.Copy(in this)
        : throw new NotSupportedException(
            $"The producer of this {Storage.Kind} frame cannot keep it past the call."
        );

    // A plane of a buffer laid out as layout says: from its offset, its rows at its stride.
    private static ReadOnlySpan<byte> Slice(
        ReadOnlySpan<byte> data,
        VideoFormat format,
        PlaneLayout layout,
        int plane
    )
    {
        if (plane >= layout.Count)
        {
            return default;
        }

        VideoPlane where = layout[plane];
        int rows = PlaneLayout.PlaneRows(format.PixelFormat, plane, format.CodedSize.Height);
        int length = Math.Min(rows * where.Stride, data.Length - where.Offset);
        return data.Slice(where.Offset, length);
    }

    // The same strides with each plane starting its own span.
    private static PlaneLayout Unpacked(PlaneLayout layout)
    {
        Span<VideoPlane> planes = stackalloc VideoPlane[layout.Count];
        for (int i = 0; i < layout.Count; i++)
        {
            planes[i] = new VideoPlane(0, layout[i].Stride);
        }

        return new PlaneLayout(planes);
    }
}

/// <summary>Keeps frames past the call that delivered them, in the producer's own way.</summary>
public interface IVideoFrameRetainer
{
    /// <summary>Keeps a frame this producer delivered.</summary>
    /// <param name="frame">The frame.</param>
    /// <returns>An owning lease.</returns>
    VideoFrameLease Retain(in VideoFrame frame);
}

/// <summary>
/// An owned frame. Disposing it releases the buffer (or signals the release point) exactly once;
/// using <see cref="Frame"/> afterwards throws <see cref="ObjectDisposedException"/>. A holder that reads
/// the frame on a GPU keeps the lease until that work has finished, since the producer may reuse the
/// surface as soon as the lease is released.
/// </summary>
public abstract class VideoFrameLease : IDisposable
{
    private int _disposed;

    // The holders of the frame: this lease and the shared leases made of it. The frame is released when
    // the last one goes.
    private int _holders = 1;

    /// <summary>A lease on a frame's description.</summary>
    /// <param name="storage">Where the pixels are.</param>
    /// <param name="format">The pixel format and geometry.</param>
    /// <param name="timestamp">When the frame was made.</param>
    /// <param name="color">How samples map to colours.</param>
    /// <param name="orientation">How to turn the buffer upright.</param>
    /// <param name="duration">How long the frame is shown.</param>
    protected VideoFrameLease(
        VideoStorage storage,
        VideoFormat format,
        MediaTimestamp timestamp,
        VideoColor color,
        VideoOrientation orientation,
        TimeSpan duration
    )
    {
        Storage = storage;
        Format = format;
        Timestamp = timestamp;
        Color = color;
        Orientation = orientation;
        Duration = duration;
    }

    /// <summary>Where the pixels are.</summary>
    public VideoStorage Storage { get; }

    /// <summary>The pixel format and geometry.</summary>
    public VideoFormat Format { get; }

    /// <summary>When the frame was made.</summary>
    public MediaTimestamp Timestamp { get; }

    /// <summary>How samples map to colours.</summary>
    public VideoColor Color { get; }

    /// <summary>How to turn the buffer upright.</summary>
    public VideoOrientation Orientation { get; }

    /// <summary>How long the frame is shown.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Whether the lease has been released.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>The frame, as a borrowed view that is valid while the lease is.</summary>
    /// <exception cref="ObjectDisposedException">The lease was released.</exception>
    public VideoFrame Frame
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (!Storage.TryGetValue(out CpuImage image))
            {
                return new VideoFrame(
                    Storage,
                    Format,
                    Timestamp,
                    default,
                    Color,
                    Orientation,
                    Duration,
                    KeepAgain
                );
            }

            PlaneLayout planes = image.Planes;
            return new VideoFrame(
                Format,
                Timestamp,
                GetPlane(0),
                planes[0].Stride,
                planes.Count > 1 ? GetPlane(1) : default,
                planes.Count > 1 ? planes[1].Stride : 0,
                planes.Count > 2 ? GetPlane(2) : default,
                planes.Count > 2 ? planes[2].Stride : 0,
                planes.Count > 3 ? GetPlane(3) : default,
                planes.Count > 3 ? planes[3].Stride : 0,
                Color,
                Orientation,
                Duration,
                KeepAgain
            );
        }
    }

    /// <summary>
    /// A plane of a CPU frame, from its first row, at the stride <see cref="Storage"/> gives; a lease
    /// on a CPU frame overrides it.
    /// </summary>
    /// <param name="index">Which plane.</param>
    /// <returns>The plane's bytes.</returns>
    protected virtual ReadOnlySpan<byte> GetPlane(int index) =>
        throw new NotSupportedException(
            $"This lease holds a {Storage.Kind} frame, which has no CPU planes."
        );

    /// <summary>
    /// How the frame is kept again when a consumer of <see cref="Frame"/> retains it. By default the lease
    /// is shared: the new lease holds this one's frame, released when every holder has let go. A producer
    /// with its own way of keeping frames (a pooled surface held again) overrides it.
    /// </summary>
    protected virtual IVideoFrameRetainer? KeepAgain => new Sharer(this);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            LetGo();
            GC.SuppressFinalize(this);
        }
    }

    private void LetGo()
    {
        if (Interlocked.Decrement(ref _holders) == 0)
        {
            Release();
        }
    }

    // Makes shared leases of a lease that is still held.
    private sealed class Sharer(VideoFrameLease owner) : IVideoFrameRetainer
    {
        public VideoFrameLease Retain(in VideoFrame frame)
        {
            int holders;
            do
            {
                holders = Volatile.Read(ref owner._holders);
                if (holders == 0)
                {
                    throw new ObjectDisposedException(owner.GetType().Name);
                }
            } while (
                Interlocked.CompareExchange(ref owner._holders, holders + 1, holders) != holders
            );

            return new SharedLease(owner);
        }
    }

    // Another holder of an owner's frame, reading it through the owner.
    private sealed class SharedLease(VideoFrameLease owner)
        : VideoFrameLease(
            owner.Storage,
            owner.Format,
            owner.Timestamp,
            owner.Color,
            owner.Orientation,
            owner.Duration
        )
    {
        protected override ReadOnlySpan<byte> GetPlane(int index) => owner.GetPlane(index);

        protected override IVideoFrameRetainer? KeepAgain => new Sharer(owner);

        protected override void Release() => owner.LetGo();
    }

    /// <summary>Releases the frame's buffer; called once.</summary>
    protected abstract void Release();
}

// A CPU frame copied into memory rented from the shared pool, its planes back to back at their strides.
internal sealed class CpuVideoFrameLease : VideoFrameLease
{
    private readonly byte[] _buffer;
    private readonly int _length;

    private CpuVideoFrameLease(in VideoFrame frame, PlaneLayout layout, byte[] buffer, int length)
        : base(
            new CpuImage(layout),
            frame.Format,
            frame.Timestamp,
            frame.Color,
            frame.Orientation,
            frame.Duration
        )
    {
        _buffer = buffer;
        _length = length;
        for (int i = 0; i < frame.PlaneCount; i++)
        {
            frame.GetPlane(i).CopyTo(buffer.AsSpan(layout[i].Offset));
        }
    }

    public static CpuVideoFrameLease Copy(in VideoFrame frame)
    {
        _ = frame.Storage.TryGetValue(out CpuImage image);
        Span<VideoPlane> planes = stackalloc VideoPlane[frame.PlaneCount];
        int offset = 0;
        for (int i = 0; i < planes.Length; i++)
        {
            planes[i] = new VideoPlane(offset, image.Planes[i].Stride);
            offset += frame.GetPlane(i).Length;
        }

        return new CpuVideoFrameLease(
            in frame,
            new PlaneLayout(planes),
            ArrayPool<byte>.Shared.Rent(offset),
            offset
        );
    }

    protected override ReadOnlySpan<byte> GetPlane(int index)
    {
        _ = Storage.TryGetValue(out CpuImage image);
        int start = image.Planes[index].Offset;
        int end = index + 1 < image.Planes.Count ? image.Planes[index + 1].Offset : _length;
        return _buffer.AsSpan(start, end - start);
    }

    protected override void Release() => ArrayPool<byte>.Shared.Return(_buffer);
}

/// <summary>Receives frames from a source on the source's thread.</summary>
public interface IVideoFrameConsumer
{
    /// <summary>
    /// A frame. It is valid only during the call: a consumer finishes with it, or calls
    /// <see cref="VideoFrame.Retain"/>, before returning.
    /// </summary>
    /// <param name="frame">The frame.</param>
    void OnFrame(in VideoFrame frame);
}

/// <summary>
/// What a consumer accepts, stated before the first frame so the source can produce it directly: the
/// storages, formats and GPU it can take without a conversion.
/// </summary>
/// <param name="Storages">Storages accepted, most preferred first.</param>
/// <param name="PixelFormats">Pixel formats accepted, most preferred first.</param>
/// <param name="Device">The GPU a GPU frame must be on; null for any.</param>
/// <param name="DrmModifiers">DRM format modifiers accepted for DMA-BUF storage; empty for linear only.</param>
/// <param name="HeldFrames">
/// How many frames the consumer keeps past the call at most (a playout queue), which a producer with a
/// fixed pool of surfaces (a hardware decoder) allocates beyond its own needs.
/// </param>
public sealed record VideoConstraints(
    ImmutableArray<VideoStorageKind> Storages,
    ImmutableArray<PixelFormat> PixelFormats,
    GpuIdentity? Device = null,
    ImmutableArray<ulong> DrmModifiers = default,
    int HeldFrames = 0
)
{
    /// <summary>CPU frames in the given formats.</summary>
    /// <param name="formats">The formats, most preferred first.</param>
    /// <returns>The constraints.</returns>
    public static VideoConstraints Cpu(params ImmutableArray<PixelFormat> formats) =>
        new([VideoStorageKind.Cpu], formats);

    /// <summary>Whether a frame's storage and format are acceptable.</summary>
    /// <param name="storage">The storage.</param>
    /// <param name="format">The pixel format.</param>
    /// <returns>Whether the consumer takes it as it is.</returns>
    public bool Accepts(VideoStorage storage, PixelFormat format) =>
        Storages.Contains(storage.Kind)
        && PixelFormats.Contains(format)
        && (Device is null || storage.Device is null || storage.Device == Device);
}

/// <summary>
/// A source of video frames: a capture, a decoder, a shared texture. Sources push: they call their
/// consumer as frames are made, on their own thread, and never wait to be polled.
/// </summary>
public interface IVideoSource
{
    /// <summary>
    /// Starts delivering frames that meet the constraints, as closely as the source can, to a consumer.
    /// </summary>
    /// <param name="consumer">Where frames go.</param>
    /// <param name="constraints">What the consumer accepts.</param>
    /// <returns>A handle that stops delivery when disposed.</returns>
    IDisposable Connect(IVideoFrameConsumer consumer, VideoConstraints constraints);
}
