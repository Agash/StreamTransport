using Agash.StreamTransport.Media;
using Agash.StreamTransport.Windows.Direct3D12;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spout2.NET;

namespace Agash.StreamTransport.Windows.Spout;

/// <summary>
/// Publishes frames as a Spout sender. It takes 8-bit BGRA or RGBA Direct3D 12 textures on its GPU, and
/// lends Spout's shared texture for a processor to draw a frame straight into
/// (<see cref="TryRender{TState}"/>); a frame handed to <see cref="OnFrame"/> is copied into it on the GPU
/// before the call returns.
/// </summary>
public sealed partial class SpoutVideoSink : IVideoSink, IDisposable
{
    private readonly D3D12Engine _engine;
    private readonly SpoutDevice _device;
    private readonly SpoutSender _sender;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>A Spout sender on a GPU.</summary>
    /// <param name="name">The sender's name, as receivers list it.</param>
    /// <param name="adapter">The GPU to share on; null for the system's default GPU.</param>
    /// <param name="loggerFactory">Where the sink and Spout log.</param>
    public SpoutVideoSink(
        string name,
        GpuIdentity? adapter = null,
        ILoggerFactory? loggerFactory = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ILoggerFactory loggers = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = loggers.CreateLogger<SpoutVideoSink>();
        nint device = D3D12Devices.Create(adapter);
        try
        {
            _engine = new D3D12Engine(device, direct: true);
        }
        finally
        {
            D3D12Engine.Release(device);
        }

        _device = SpoutDevice.FromD3D12Device(_engine.Device, _engine.Queue, loggers);
        _sender = new SpoutSender(name, _device, new SpoutSenderOptions { ShaderWritable = true });
        Constraints = new VideoConstraints(
            [VideoStorageKind.D3D12],
            [PixelFormat.Bgra, PixelFormat.Rgba],
            _engine.Adapter
        );
    }

    /// <inheritdoc/>
    public VideoConstraints Constraints { get; }

    /// <summary>The sender's name.</summary>
    public string Name => _sender.Name;

    /// <inheritdoc/>
    public void OnFrame(in VideoFrame frame)
    {
        if (!frame.Storage.TryGetValue(out D3D12Image image) || image.Adapter != _engine.Adapter)
        {
            throw new ArgumentException(
                $"The sink takes Direct3D 12 textures on its own GPU ({_engine.Adapter}); a processor makes them. The frame is {frame.Storage.Kind} on {frame.Storage.Device}.",
                nameof(frame)
            );
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Spout's copy runs on the engine's queue, so the queue waits for the frame's producer first.
            _engine.WaitForProducer(image.Sync);
            if (!_sender.Send(new D3D12Texture(image.Resource)))
            {
                LogDropped(_sender.Name);
            }
        }
    }

    /// <inheritdoc/>
    public bool TryRender<TState>(
        VideoFormat format,
        scoped in TState state,
        VideoTargetRenderer<TState> render
    )
        where TState : allows ref struct
    {
        ArgumentNullException.ThrowIfNull(render);
        SpoutFormat shared = format.PixelFormat switch
        {
            PixelFormat.Bgra => SpoutFormat.Bgra8Unorm,
            PixelFormat.Rgba => SpoutFormat.Rgba8Unorm,
            _ => SpoutFormat.Unknown,
        };
        if (shared == SpoutFormat.Unknown)
        {
            return false;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (
                !_sender.TryBeginFrame(
                    format.CodedSize.Width,
                    format.CodedSize.Height,
                    shared,
                    out SpoutSenderFrame frame
                )
            )
            {
                LogDropped(_sender.Name);
                return false;
            }

            using (frame)
            {
                // Spout's queue finishes before the texture is published, so drawing on another queue
                // makes it wait.
                VideoTarget target = new(
                    new VideoStorage(
                        new D3D12Image(
                            frame.D3D12Texture.Resource,
                            0,
                            _engine.Adapter,
                            new D3D12Sync(ReleaseQueue: _engine.Queue)
                        )
                    ),
                    format
                );
                if (!render(in target, in state))
                {
                    return false;
                }

                frame.Publish();
                return true;
            }
        }
    }

    /// <summary>Stops publishing and releases the sender.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _sender.Dispose();
            _device.Dispose();
            _engine.Dispose();
        }
    }

    [LoggerMessage(
        2310,
        LogLevel.Debug,
        "A receiver held Spout sender {Sender}'s texture too long; a frame was dropped."
    )]
    private partial void LogDropped(string sender);
}
