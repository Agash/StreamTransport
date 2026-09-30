using System.Collections.Immutable;
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
/// Frames of a Syphon server as IOSurfaces, zero-copy while a consumer's call lasts; a consumer that
/// keeps a frame gets a copy made on the GPU. Syphon carries no capture time, so frames are stamped with
/// when they were observed. One client serves every connected consumer and runs while any is connected.
/// </summary>
/// <remarks>
/// Find servers with Syphon.NET's <see cref="SyphonServerDirectory"/>, which needs the main run loop: an
/// AppKit application serves it already, and a console host runs its work in <see cref="SyphonMainLoop"/>.
/// </remarks>
public sealed partial class SyphonVideoSource : IVideoSource, IDisposable
{
    private readonly SyphonServerDescription _server;
    private readonly GpuIdentity? _device;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private ImmutableArray<IVideoFrameConsumer> _consumers = [];
    private Receiving? _receiving;

    /// <summary>A source of a Syphon server's frames.</summary>
    /// <param name="server">The server, as a directory describes it.</param>
    /// <param name="device">The GPU to receive on; null for the system's default GPU.</param>
    /// <param name="loggerFactory">Where the source and Syphon log.</param>
    public SyphonVideoSource(
        SyphonServerDescription server,
        GpuIdentity? device = null,
        ILoggerFactory? loggerFactory = null
    )
    {
        ArgumentNullException.ThrowIfNull(server);
        _server = server;
        _device = device;
        _loggers = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggers.CreateLogger<SyphonVideoSource>();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Frames are the server's own: 8-bit BGRA IOSurfaces. What a consumer needs beyond that, a
    /// processor makes.
    /// </remarks>
    public IDisposable Connect(IVideoFrameConsumer consumer, VideoConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        lock (_gate)
        {
            _receiving ??= new Receiving(this);
            _consumers = _consumers.Add(consumer);
        }

        return new Connection(this, consumer);
    }

    /// <summary>Stops receiving and disconnects every consumer.</summary>
    public void Dispose()
    {
        Receiving? receiving;
        lock (_gate)
        {
            receiving = _receiving;
            _receiving = null;
            _consumers = [];
        }

        receiving?.Dispose();
    }

    private void Disconnect(IVideoFrameConsumer consumer)
    {
        Receiving? stopped = null;
        lock (_gate)
        {
            _consumers = _consumers.Remove(consumer);
            if (_consumers.IsEmpty)
            {
                stopped = _receiving;
                _receiving = null;
            }
        }

        stopped?.Dispose();
    }

    [LoggerMessage(2400, LogLevel.Information, "Receiving Syphon server {Server} on {Device}.")]
    private partial void LogReceiving(string server, GpuIdentity device);

    [LoggerMessage(2401, LogLevel.Information, "Syphon server {Server} stopped.")]
    private partial void LogServerStopped(string server);

    [LoggerMessage(2402, LogLevel.Error, "Receiving Syphon server {Server} stopped with an error.")]
    private partial void LogReceiveFailed(Exception exception, string server);

    [LoggerMessage(2403, LogLevel.Warning, "A consumer failed to take a Syphon frame.")]
    private partial void LogConsumerFailed(Exception exception);

    private sealed class Connection(SyphonVideoSource source, IVideoFrameConsumer consumer)
        : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                source.Disconnect(consumer);
            }
        }
    }

    // One Syphon client on a Metal device of the source's, handing frames to the consumers.
    private sealed class Receiving : IVideoFrameRetainer, IDisposable
    {
        private readonly SyphonVideoSource _source;
        private readonly MetalEngine _engine;
        private readonly SyphonClient _client;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private IOSurfacePool? _copies;

        public Receiving(SyphonVideoSource source)
        {
            _source = source;
            _engine = MetalEngine.For(source._device);
            _client = new SyphonClient(
                source._server,
                new SyphonClientOptions { Device = _engine.Device, LoggerFactory = source._loggers }
            );
            source.LogReceiving(source._server.Name, _engine.Identity);
            _loop = RunAsync();
        }

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                _loop.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Deliberately not logged: cancellation is how receiving stops.
            }

            _client.Dispose();
            _copies?.Dispose();
            _stop.Dispose();
        }

        // A kept frame is copied on the GPU into a surface of the source's: Syphon has no lock, and the
        // server renders its next frame into the same surface.
        public VideoFrameLease Retain(in VideoFrame frame)
        {
            _ = frame.Storage.TryGetValue(out IOSurfaceImage image);
            VideoSize size = new(frame.Format.CodedSize.Width, frame.Format.CodedSize.Height);
            if (_copies is not { } pool || !pool.Makes(PixelFormat.Bgra, size))
            {
                _copies?.Dispose();
                _copies = pool = new IOSurfacePool(PixelFormat.Bgra, size);
            }

            using NSAutoreleasePool autoreleased = new();
            PooledSurface copy = pool.Rent();
            try
            {
                IOSurface.IOSurface served = Runtime.GetINativeObject<IOSurface.IOSurface>(
                    image.Surface,
                    false
                )!;
                using IMTLTexture from = Bgra(served, size);
                using IMTLTexture to = Bgra(copy.Surface, size);
                using IMTLCommandBuffer commands = _engine.Begin();
                using IMTLBlitCommandEncoder blit =
                    commands.BlitCommandEncoder
                    ?? throw new InvalidOperationException(
                        "The command buffer gave no blit encoder."
                    );
                blit.CopyFromTexture(
                    from,
                    0,
                    0,
                    new MTLOrigin(0, 0, 0),
                    new MTLSize(size.Width, size.Height, 1),
                    to,
                    0,
                    0,
                    new MTLOrigin(0, 0, 0)
                );
                blit.EndEncoding();
                MetalEngine.Complete(commands);
            }
            catch
            {
                copy.Release();
                throw;
            }

            VideoFrame kept = new(
                new VideoStorage(new IOSurfaceImage(copy.Handle, _engine.Identity)),
                frame.Format,
                frame.Timestamp,
                color: frame.Color,
                orientation: frame.Orientation,
                duration: frame.Duration
            );
            return new IOSurfaceFrameLease(in kept, copy);
        }

        private IMTLTexture Bgra(IOSurface.IOSurface surface, VideoSize size) =>
            _engine.Texture(
                surface,
                0,
                MTLPixelFormat.BGRA8Unorm,
                size.Width,
                size.Height,
                MTLTextureUsage.ShaderRead
            );

        private Task RunAsync() =>
            _client
                .RunAsync(Deliver, _stop.Token)
                .ContinueWith(
                    task =>
                    {
                        if (task.Exception is { } failure)
                        {
                            _source.LogReceiveFailed(
                                failure.GetBaseException(),
                                _source._server.Name
                            );
                        }
                        else if (!task.IsCanceled)
                        {
                            _source.LogServerStopped(_source._server.Name);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );

        private void Deliver(in SyphonFrame frame)
        {
            if (!frame.IsNew)
            {
                return;
            }

            using NSAutoreleasePool autoreleased = new();
            VideoFrame video = new(
                new VideoStorage(new IOSurfaceImage(frame.Surface.Handle, _engine.Identity)),
                new VideoFormat(PixelFormat.Bgra, frame.Width, frame.Height),
                MediaTimestamp.Observed(new MediaTime(frame.ObservedAtNanoseconds)),
                color: VideoColor.Srgb,
                retainer: this
            );
            foreach (IVideoFrameConsumer consumer in _source._consumers)
            {
                try
                {
                    consumer.OnFrame(in video);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // One consumer's failure does not stop the others or the client.
                    _source.LogConsumerFailed(exception);
                }
            }
        }
    }
}
