using Agash.StreamTransport.Media;
using CoreVideo;

namespace Agash.StreamTransport.MacOS.Metal;

/// <summary>
/// IOSurfaces of one format and size from a Core Video pixel-buffer pool, which lays out planes as
/// VideoToolbox and Metal expect and recycles surfaces once nothing holds them.
/// </summary>
internal sealed class IOSurfacePool : IDisposable
{
    private readonly CVPixelBufferPool _pool;

    public IOSurfacePool(PixelFormat format, VideoSize size, ColorRange range = ColorRange.Limited)
    {
        Format = format;
        Size = size;
        CVPixelBufferPoolSettings settings = new() { MinimumBufferCount = 3 };
        CVPixelBufferAttributes attributes = new()
        {
            PixelFormatType = PixelFormatType(format, range),
            Width = size.Width,
            Height = size.Height,
            AllocateWithIOSurface = true,
            MetalCompatibility = true,
        };
        _pool = new CVPixelBufferPool(settings, attributes);
    }

    public PixelFormat Format { get; }

    public VideoSize Size { get; }

    /// <summary>A surface, held once; release it when done.</summary>
    /// <returns>The surface.</returns>
    public PooledSurface Rent()
    {
        CVPixelBuffer buffer =
            _pool.CreatePixelBuffer()
            ?? throw new InvalidOperationException("The pixel-buffer pool gave no buffer.");
        IOSurface.IOSurface surface =
            buffer.GetIOSurface()
            ?? throw new InvalidOperationException("The pixel buffer has no IOSurface.");
        return new PooledSurface(buffer, surface);
    }

    public void Dispose() => _pool.Dispose();

    /// <summary>Whether this pool makes surfaces of a format and size.</summary>
    /// <param name="format">The format.</param>
    /// <param name="size">The size.</param>
    /// <returns>Whether it does.</returns>
    public bool Makes(PixelFormat format, VideoSize size) => Format == format && Size == size;

    private static CVPixelFormatType PixelFormatType(PixelFormat format, ColorRange range) =>
        format switch
        {
            PixelFormat.Nv12 when range == ColorRange.Full =>
                CVPixelFormatType.CV420YpCbCr8BiPlanarFullRange,
            PixelFormat.Nv12 => CVPixelFormatType.CV420YpCbCr8BiPlanarVideoRange,
            PixelFormat.Bgra => CVPixelFormatType.CV32BGRA,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };
}

/// <summary>
/// A pooled IOSurface, counted: the processor holds it while it delivers the surface, and each kept
/// frame holds it again. It returns to the pool when the last holder releases it.
/// </summary>
internal sealed class PooledSurface(CVPixelBuffer buffer, IOSurface.IOSurface surface)
{
    private int _holds = 1;

    public IOSurface.IOSurface Surface { get; } = surface;

    public nint Handle => Surface.Handle;

    public void Hold() => Interlocked.Increment(ref _holds);

    public void Release()
    {
        if (Interlocked.Decrement(ref _holds) == 0)
        {
            Surface.Dispose();
            buffer.Dispose();
        }
    }
}

/// <summary>A frame on a pooled surface, kept by a consumer; the surface returns to its pool on release.</summary>
internal sealed class IOSurfaceFrameLease : VideoFrameLease, IVideoFrameRetainer
{
    private readonly PooledSurface _surface;

    public IOSurfaceFrameLease(in VideoFrame frame, PooledSurface surface)
        : base(
            frame.Storage,
            frame.Format,
            frame.Timestamp,
            frame.Color,
            frame.Orientation,
            frame.Duration
        )
    {
        _surface = surface;
    }

    protected override IVideoFrameRetainer KeepAgain => this;

    public VideoFrameLease Retain(in VideoFrame frame)
    {
        _surface.Hold();
        return new IOSurfaceFrameLease(in frame, _surface);
    }

    protected override void Release() => _surface.Release();
}
