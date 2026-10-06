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
/// Frames of a Syphon server as IOSurfaces. Syphon has no lock and the server renders every frame into
/// one surface, so each new frame is copied once on the GPU into a surface of the source's and handed on
/// when the copy completes: a whole frame, kept by holding it, with no thread waiting for the GPU.
/// Syphon carries no capture time, so frames are stamped with when they were observed. One client serves
/// every connected consumer and runs while any is connected.
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

    [LoggerMessage(
        2404,
        LogLevel.Error,
        "Copying a frame of Syphon server {Server} failed on the GPU: {Failure}"
    )]
    private partial void LogCopyFailed(string server, string failure);

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
        private readonly CommandCompletions _completions = new();
        private IOSurfacePool? _copies;
        private PooledSurface? _delivering;

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

        // Called by a consumer inside OnFrame, on the delivering thread: the copy it was handed is held.
        public VideoFrameLease Retain(in VideoFrame frame)
        {
            PooledSurface copy =
                _delivering
                ?? throw new InvalidOperationException("Frames are retained only while delivered.");
            copy.Hold();
            return new IOSurfaceFrameLease(in frame, copy);
        }

        // A new frame is copied on the GPU into a surface of the source's at once: Syphon has no lock,
        // and the server renders its next frame into the same surface. The copy is handed on when it
        // completes, so consumers get a whole frame they may keep and nothing waits for the GPU.
        private void Copy(in SyphonFrame frame)
        {
            VideoSize size = new(frame.Width, frame.Height);
            if (_copies is not { } pool || !pool.Makes(PixelFormat.Bgra, size))
            {
                _copies?.Dispose();
                _copies = pool = new IOSurfacePool(PixelFormat.Bgra, size);
            }

            PooledSurface copy = pool.Rent();
            try
            {
                using IMTLTexture from = Bgra(frame.Surface, size);
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
                long observedAt = frame.ObservedAtNanoseconds;
                _completions.Commit(commands, failure => HandOn(copy, size, observedAt, failure));
            }
            catch
            {
                copy.Release();
                throw;
            }
        }

        // Runs once a copy completed, in order: the copy goes to every consumer.
        private void HandOn(PooledSurface copy, VideoSize size, long observedAt, string? failure)
        {
            try
            {
                if (failure is not null)
                {
                    _source.LogCopyFailed(_source._server.Name, failure);
                    return;
                }

                using NSAutoreleasePool autoreleased = new();
                VideoFrame video = new(
                    new VideoStorage(new IOSurfaceImage(copy.Handle, _engine.Identity)),
                    new VideoFormat(PixelFormat.Bgra, size.Width, size.Height),
                    MediaTimestamp.Observed(new MediaTime(observedAt)),
                    color: VideoColor.Srgb,
                    retainer: this
                );
                _delivering = copy;
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
            finally
            {
                _delivering = null;
                copy.Release();
            }
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
            if (frame.IsNew)
            {
                using NSAutoreleasePool autoreleased = new();
                Copy(in frame);
            }
        }
    }
}
