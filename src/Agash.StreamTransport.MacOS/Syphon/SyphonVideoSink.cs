using Agash.StreamTransport.MacOS.Metal;
using Agash.StreamTransport.Media;
using Foundation;
using Metal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ObjCRuntime;
using Syphon.NET;

namespace Agash.StreamTransport.MacOS.Syphon;

/// <summary>
/// Publishes frames as a Syphon server. It takes 8-bit BGRA frames: IOSurfaces on its GPU, which a
/// processor makes from what a decoder produces, or frames in memory. It lends the server's surface for
/// a processor to draw a frame straight into (<see cref="Render{TState}"/>); a frame handed to
/// <see cref="OnFrame"/> is copied into it before the call returns.
/// </summary>
public sealed class SyphonVideoSink : IVideoSink, IDisposable
{
    private readonly MetalEngine _engine;
    private readonly SyphonServer _server;
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>A Syphon server on a GPU.</summary>
    /// <param name="name">The server's name, as clients list it.</param>
    /// <param name="device">The GPU to publish on; null for the system's default GPU.</param>
    /// <param name="loggerFactory">Where the Syphon server logs.</param>
    public SyphonVideoSink(
        string name,
        GpuIdentity? device = null,
        ILoggerFactory? loggerFactory = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ILoggerFactory loggers = loggerFactory ?? NullLoggerFactory.Instance;
        _engine = MetalEngine.For(device);
        _server = new SyphonServer(name, new SyphonServerOptions { LoggerFactory = loggers });
        Constraints = new VideoConstraints(
            [VideoStorageKind.IOSurface, VideoStorageKind.Cpu],
            [PixelFormat.Bgra],
            _engine.Identity
        );
    }

    /// <inheritdoc/>
    public VideoConstraints Constraints { get; }

    /// <summary>The server's name.</summary>
    public string Name => _server.Name;

    /// <summary>What clients see of the server, to hand to a client in another process.</summary>
    public SyphonServerDescription Description => _server.Description;

    /// <summary>Whether any client is receiving.</summary>
    public bool HasClients => _server.HasClients;

    /// <inheritdoc/>
    public void OnFrame(in VideoFrame frame)
    {
        if (
            frame.Format.PixelFormat != PixelFormat.Bgra
            || !Constraints.Accepts(frame.Storage, PixelFormat.Bgra)
        )
        {
            throw new ArgumentException(
                $"The sink takes BGRA IOSurfaces on its own GPU ({_engine.Identity}) or BGRA in memory; a processor makes them. The frame is {frame.Format.PixelFormat} {frame.Storage.Kind} on {frame.Storage.Device}.",
                nameof(frame)
            );
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            VideoRect visible = frame.Format.VisibleRect;
            if (frame.Storage.TryGetValue(out IOSurfaceImage image))
            {
                Publish(image, visible);
            }
            else
            {
                _ = frame.Storage.TryGetValue(out CpuImage cpu);
                int stride = cpu.Planes[0].Stride;
                _server.PublishPixels(
                    frame.GetPlane(0)[((visible.Y * stride) + (visible.X * 4))..],
                    visible.Width,
                    visible.Height,
                    stride
                );
            }
        }
    }

    /// <inheritdoc/>
    public VideoRenderResult Render<TState>(
        VideoFormat format,
        scoped in TState state,
        VideoTargetRenderer<TState> render
    )
        where TState : allows ref struct
    {
        ArgumentNullException.ThrowIfNull(render);
        if (format.PixelFormat != PixelFormat.Bgra)
        {
            return VideoRenderResult.Unavailable;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Syphon has no lock on its surface: the renderer finishes drawing before it returns, and
            // the frame is published after.
            using SyphonServerFrame frame = _server.BeginFrame(
                format.CodedSize.Width,
                format.CodedSize.Height
            );
            VideoTarget target = new(
                new VideoStorage(new IOSurfaceImage(frame.Surface.Handle, _engine.Identity)),
                format
            );
            if (!render(in target, in state))
            {
                return VideoRenderResult.Unavailable;
            }

            frame.Publish();
            return VideoRenderResult.Rendered;
        }
    }

    /// <summary>Stops publishing and retires the server.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                _server.Dispose();
            }
        }
    }

    // A GPU copy into the server's surface, finished before the frame goes back to its producer.
    private void Publish(IOSurfaceImage image, VideoRect visible)
    {
        using NSAutoreleasePool autoreleased = new();
        IOSurface.IOSurface surface = Runtime.GetINativeObject<IOSurface.IOSurface>(
            image.Surface,
            false
        )!;
        using IMTLTexture texture = _engine.Texture(
            surface,
            0,
            MTLPixelFormat.BGRA8Unorm,
            (int)surface.Width,
            (int)surface.Height,
            MTLTextureUsage.ShaderRead
        );
        using IMTLCommandBuffer commands = _engine.Begin(image.SharedEvent, image.SignalValue);
        _server.PublishTexture(
            texture,
            commands,
            new MTLRegion(
                new MTLOrigin(visible.X, visible.Y, 0),
                new MTLSize(visible.Width, visible.Height, 1)
            )
        );
        MetalEngine.Complete(commands);
    }
}
