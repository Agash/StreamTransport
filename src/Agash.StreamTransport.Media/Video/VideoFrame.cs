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
    private readonly IVideoFrameRetainer? _retainer;

    /// <summary>A frame.</summary>
    /// <param name="storage">Where the pixels are.</param>
    /// <param name="format">The pixel format and geometry.</param>
    /// <param name="timestamp">When the frame was made.</param>
    /// <param name="cpuData">The pixels, for <see cref="VideoStorageKind.Cpu"/> storage.</param>
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
        if (storage.Kind == VideoStorageKind.Cpu && cpuData.IsEmpty)
        {
            throw new ArgumentException("A CPU frame needs its pixels.", nameof(cpuData));
        }

        Storage = storage;
        Format = format;
        Timestamp = timestamp;
        CpuData = cpuData;
        Color = color;
        Orientation = orientation;
        Duration = duration;
        _retainer = retainer;
    }

    /// <summary>Where the pixels are.</summary>
    public VideoStorage Storage { get; }

    /// <summary>The pixel format and geometry.</summary>
    public VideoFormat Format { get; }

    /// <summary>When the frame was made.</summary>
    public MediaTimestamp Timestamp { get; }

    /// <summary>The pixels of a CPU frame, laid out as its <see cref="CpuImage.Planes"/> say; empty otherwise.</summary>
    public ReadOnlySpan<byte> CpuData { get; }

    /// <summary>How samples map to colours.</summary>
    public VideoColor Color { get; }

    /// <summary>How to turn the buffer upright.</summary>
    public VideoOrientation Orientation { get; }

    /// <summary>How long the frame is shown; zero when unknown.</summary>
    public TimeSpan Duration { get; }

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
/// using <see cref="Frame"/> afterwards throws <see cref="ObjectDisposedException"/>.
/// </summary>
public abstract class VideoFrameLease : IDisposable
{
    private int _disposed;

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
            return new VideoFrame(
                Storage,
                Format,
                Timestamp,
                CpuData,
                Color,
                Orientation,
                Duration,
                KeepAgain
            );
        }
    }

    /// <summary>The pixels of a CPU frame; empty otherwise.</summary>
    protected virtual ReadOnlySpan<byte> CpuData => default;

    /// <summary>How the frame is kept again when a consumer of <see cref="Frame"/> retains it.</summary>
    protected virtual IVideoFrameRetainer? KeepAgain => null;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Release();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>Releases the frame's buffer; called once.</summary>
    protected abstract void Release();
}

// A CPU frame copied into memory rented from the shared pool.
internal sealed class CpuVideoFrameLease : VideoFrameLease
{
    private readonly byte[] _buffer;
    private readonly int _length;

    private CpuVideoFrameLease(in VideoFrame frame, byte[] buffer)
        : base(
            frame.Storage,
            frame.Format,
            frame.Timestamp,
            frame.Color,
            frame.Orientation,
            frame.Duration
        )
    {
        _buffer = buffer;
        _length = frame.CpuData.Length;
        frame.CpuData.CopyTo(buffer);
    }

    public static CpuVideoFrameLease Copy(in VideoFrame frame) =>
        new(in frame, ArrayPool<byte>.Shared.Rent(frame.CpuData.Length));

    protected override ReadOnlySpan<byte> CpuData => _buffer.AsSpan(0, _length);

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
public sealed record VideoConstraints(
    ImmutableArray<VideoStorageKind> Storages,
    ImmutableArray<PixelFormat> PixelFormats,
    GpuIdentity? Device = null,
    ImmutableArray<ulong> DrmModifiers = default
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
