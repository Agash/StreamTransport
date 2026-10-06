using System.Collections.Immutable;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.Windows.Direct3D12;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spout2.NET;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Agash.StreamTransport.Windows.Spout;

/// <summary>How a <see cref="SpoutVideoSource"/> receives.</summary>
public sealed record SpoutVideoSourceOptions
{
    /// <summary>The sender to receive; null follows Spout's active sender.</summary>
    public string? SenderName { get; init; }

    /// <summary>The GPU to receive on, which must be the sender's; null for the system's default GPU.</summary>
    public GpuIdentity? Adapter { get; init; }

    /// <summary>Wait for the sender's frame event, for senders that signal one.</summary>
    public bool WaitForFrameSync { get; init; }
}

/// <summary>
/// Frames of a Spout sender as Direct3D 12 textures, zero-copy while a consumer's call lasts; a consumer
/// that keeps a frame gets a copy made on the GPU. Spout carries no capture time, so frames are stamped
/// with when they were observed. One receiver serves every connected consumer and runs while any is
/// connected.
/// </summary>
public sealed partial class SpoutVideoSource : IVideoSource, IDisposable
{
    private readonly SpoutVideoSourceOptions _options;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private ImmutableArray<IVideoFrameConsumer> _consumers = [];
    private Receiving? _receiving;

    /// <summary>A source of a Spout sender's frames.</summary>
    /// <param name="options">What to receive; defaults when null.</param>
    /// <param name="loggerFactory">Where the source and Spout log.</param>
    public SpoutVideoSource(
        SpoutVideoSourceOptions? options = null,
        ILoggerFactory? loggerFactory = null
    )
    {
        _options = options ?? new SpoutVideoSourceOptions();
        _loggers = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggers.CreateLogger<SpoutVideoSource>();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Frames are the sender's own: 8-bit BGRA or RGBA textures on the sender's GPU. What a consumer needs
    /// beyond that, a processor makes.
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

    [LoggerMessage(2300, LogLevel.Information, "Receiving Spout on {Adapter}.")]
    private partial void LogReceiving(GpuIdentity adapter);

    [LoggerMessage(
        2301,
        LogLevel.Warning,
        "Spout sender {Sender} sends {Format}, which is not 8-bit RGB; its frames are skipped."
    )]
    private partial void LogUnsupportedFormat(string sender, SpoutFormat format);

    [LoggerMessage(2302, LogLevel.Error, "Receiving Spout stopped with an error.")]
    private partial void LogReceiveFailed(Exception exception);

    [LoggerMessage(2303, LogLevel.Warning, "A consumer failed to take a Spout frame.")]
    private partial void LogConsumerFailed(Exception exception);

    private sealed class Connection(SpoutVideoSource source, IVideoFrameConsumer consumer)
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

    // One Spout receiver on a Direct3D 12 device of the source's, handing frames to the consumers.
    private sealed class Receiving : IVideoFrameRetainer, IDisposable
    {
        private readonly SpoutVideoSource _source;
        private readonly D3D12Engine _engine;
        private readonly SpoutDevice _device;
        private readonly SpoutReceiver _receiver;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private D3D12TexturePool? _copies;
        private SpoutFormat _refused;

        public Receiving(SpoutVideoSource source)
        {
            _source = source;
            nint device = D3D12Devices.Create(source._options.Adapter);
            try
            {
                // A direct queue: Spout copies on it, and waits for what is submitted to it before it
                // lets the sender reuse its texture.
                _engine = new D3D12Engine(device, direct: true);
            }
            finally
            {
                D3D12Engine.Release(device);
            }

            _device = SpoutDevice.FromD3D12Device(_engine.Device, _engine.Queue, source._loggers);
            _receiver = new SpoutReceiver(
                _device,
                new SpoutReceiverOptions
                {
                    SenderName = source._options.SenderName,
                    WaitForFrameSync = source._options.WaitForFrameSync,
                }
            );
            source.LogReceiving(_engine.Adapter);
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

            _receiver.Dispose();
            _device.Dispose();
            _copies?.Dispose();
            _engine.Dispose();
            _stop.Dispose();
        }

        // A kept frame is copied, on Spout's queue, into a texture of the source's: Spout waits for that
        // queue before the sender may write its texture again, so the copy reads the frame as delivered.
        public unsafe VideoFrameLease Retain(in VideoFrame frame)
        {
            _ = frame.Storage.TryGetValue(out D3D12Image image);
            DXGI_FORMAT format = Unorm(D3D12Engine.Describe(image.Resource).Format);
            VideoSize size = new(frame.Format.VisibleRect.Width, frame.Format.VisibleRect.Height);
            if (
                _copies is not { } pool
                || pool.Format != format
                || pool.Width != size.Width
                || pool.Height != size.Height
            )
            {
                _copies?.Dispose();
                _copies = pool = new D3D12TexturePool(
                    _engine,
                    format,
                    size.Width,
                    size.Height,
                    shaderWritten: false
                );
            }

            PooledTexture copy = pool.Rent();
            ID3D12GraphicsCommandList* list = _engine.BeginCopy();
            D3D12Engine.Transition(
                list,
                copy.Texture,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST
            );
            D3D12Engine.Copy(list, copy.Texture, 0, image.Resource, 0);
            D3D12Engine.Transition(
                list,
                copy.Texture,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON
            );
            ulong done = _engine.Submit();
            VideoFrame kept = new(
                new VideoStorage(
                    new D3D12Image(
                        copy.Texture,
                        0,
                        _engine.Adapter,
                        new D3D12Sync(Fence: _engine.Fence, Value: done)
                    )
                ),
                frame.Format,
                frame.Timestamp,
                color: frame.Color,
                orientation: frame.Orientation,
                duration: frame.Duration
            );
            return new D3D12FrameLease(in kept, copy);
        }

        private Task RunAsync() =>
            _receiver
                .RunAsync(Deliver, _stop.Token)
                .ContinueWith(
                    task =>
                    {
                        if (task.Exception is { } failure)
                        {
                            _source.LogReceiveFailed(failure.GetBaseException());
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );

        private void Deliver(in SpoutFrame frame)
        {
            if (!frame.IsNew)
            {
                return;
            }

            if (PixelFormatOf(frame.Format) is not { } pixelFormat)
            {
                if (_refused != frame.Format)
                {
                    _refused = frame.Format;
                    _source.LogUnsupportedFormat(frame.Sender.Name, frame.Format);
                }

                return;
            }

            // Spout finishes its queue before the sender may write the texture again, so a reader that
            // makes that queue wait for its reads reads the texture in place.
            VideoFrame video = new(
                new VideoStorage(
                    new D3D12Image(
                        frame.D3D12Texture.Resource,
                        0,
                        _engine.Adapter,
                        new D3D12Sync(ReleaseQueue: _engine.Queue)
                    )
                ),
                new VideoFormat(pixelFormat, frame.Width, frame.Height),
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
                    // One consumer's failure does not stop the others or the receiver.
                    _source.LogConsumerFailed(exception);
                }
            }
        }

        private static PixelFormat? PixelFormatOf(SpoutFormat format) =>
            format switch
            {
                SpoutFormat.Bgra8Unorm or SpoutFormat.Bgra8UnormSrgb or SpoutFormat.Bgrx8Unorm =>
                    PixelFormat.Bgra,
                SpoutFormat.Rgba8Unorm or SpoutFormat.Rgba8UnormSrgb => PixelFormat.Rgba,
                _ => null,
            };

        // Copies keep the sender's bytes in a UNORM texture of the same family, so shaders read the
        // encoded values as they are.
        private static DXGI_FORMAT Unorm(DXGI_FORMAT format) =>
            format switch
            {
                DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM_SRGB =>
                    DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB =>
                    DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
                DXGI_FORMAT.DXGI_FORMAT_B8G8R8X8_UNORM_SRGB =>
                    DXGI_FORMAT.DXGI_FORMAT_B8G8R8X8_UNORM,
                _ => format,
            };
    }
}
