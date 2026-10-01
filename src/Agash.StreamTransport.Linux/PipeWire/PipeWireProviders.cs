using System.Collections.Immutable;
using System.Globalization;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PipeWire.NET;
using PipeWire.NET.Graph;

namespace Agash.StreamTransport.Linux.PipeWire;

/// <summary>
/// One connection to the PipeWire daemon and its graph, started on first use and shared by every
/// PipeWire input and output a host makes through its providers.
/// </summary>
public sealed class PipeWireConnection : IAsyncDisposable
{
    private readonly ILoggerFactory _loggers;
    private readonly SemaphoreSlim _start = new(1, 1);
    private PipeWireContext? _context;
    private PipeWireRegistry? _registry;

    /// <summary>A connection, made on first use.</summary>
    /// <param name="loggerFactory">Where the connection logs.</param>
    public PipeWireConnection(ILoggerFactory? loggerFactory = null) =>
        _loggers = loggerFactory ?? NullLoggerFactory.Instance;

    /// <summary>The started context and the graph it sees.</summary>
    /// <param name="cancellationToken">Cancels connecting.</param>
    /// <returns>The context and registry.</returns>
    public async Task<(PipeWireContext Context, PipeWireRegistry Registry)> GetAsync(
        CancellationToken cancellationToken
    )
    {
        if (_context is { } ready && _registry is { } graph)
        {
            return (ready, graph);
        }

        await _start.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_context is null)
            {
                PipeWireContext context = new("streamtransport", _loggers);
                await context.StartAsync(cancellationToken).ConfigureAwait(false);
                PipeWireRegistry registry = new(context);
                await registry.WaitForInitialEnumerationAsync(cancellationToken).ConfigureAwait(false);
                (_context, _registry) = (context, registry);
            }

            return (_context, _registry!);
        }
        finally
        {
            _ = _start.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_registry is { } registry)
        {
            await registry.DisposeAsync().ConfigureAwait(false);
        }

        if (_context is { } context)
        {
            await context.DisposeAsync().ConfigureAwait(false);
        }

        _start.Dispose();
    }
}

/// <summary>
/// PipeWire video nodes as inputs: other applications' outputs (OBS, VTube Studio, a compositor's screen
/// capture) and the cameras PipeWire serves.
/// </summary>
/// <param name="connection">The shared daemon connection.</param>
/// <param name="loggerFactory">Where inputs log.</param>
public sealed class PipeWireVideoInputProvider(
    PipeWireConnection connection,
    ILoggerFactory? loggerFactory = null
) : IVideoInputProvider
{
    /// <inheritdoc/>
    public string Name => "pipewire";

    /// <inheritdoc/>
    public int Rank => 50;

    /// <inheritdoc/>
    public async ValueTask<ImmutableArray<VideoInputInfo>> GetInputsAsync(
        CancellationToken cancellationToken
    )
    {
        if (!OperatingSystem.IsLinux())
        {
            return [];
        }

        (_, PipeWireRegistry registry) = await connection.GetAsync(cancellationToken).ConfigureAwait(false);
        return [.. registry.Current.GetVideoSources().Select(Describe)];
    }

    // A camera PipeWire serves is keyed by its device node, so the V4L2 provider's input is the same one.
    private VideoInputInfo Describe(PipeWireNode node)
    {
        PipeWireProperties properties = node.Properties;
        string? path =
            properties.GetValueOrDefault("api.v4l2.path")
            ?? (properties.GetValueOrDefault("object.path") is { } objectPath
                && objectPath.StartsWith("v4l2:", StringComparison.Ordinal)
                ? objectPath["v4l2:".Length..]
                : null);
        bool camera =
            path is not null
            || properties.GetValueOrDefault("device.api") is "v4l2" or "libcamera"
            || properties.GetValueOrDefault("media.role") == "Camera";
        string id = node.Id.ToString(CultureInfo.InvariantCulture);
        return new VideoInputInfo(
            Name,
            id,
            node.Description ?? node.NodeName ?? id,
            camera ? MediaInputKind.Camera : MediaInputKind.Application,
            []
        )
        {
            DeviceKey = path,
        };
    }

    /// <inheritdoc/>
    public async ValueTask<IVideoInput> OpenAsync(
        VideoInputInfo input,
        VideoInputRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(request);
        (PipeWireContext context, _) = await connection.GetAsync(cancellationToken).ConfigureAwait(false);
        PipeWireVideoSourceOptions options = new()
        {
            TargetNodeId = uint.Parse(input.Id, CultureInfo.InvariantCulture),
            PreferredSize = request.Size ?? new VideoSize(1920, 1080),
            PreferredFrameRate = (int)Math.Round(request.FrameRate ?? 30),
        };
        return new VideoInput(new PipeWireVideoSource(context, options, loggerFactory), input);
    }
}

/// <summary>Received video published as a PipeWire node, for OBS or any other client to link.</summary>
/// <param name="connection">The shared daemon connection.</param>
/// <param name="loggerFactory">Where outputs log.</param>
public sealed class PipeWireVideoOutputProvider(
    PipeWireConnection connection,
    ILoggerFactory? loggerFactory = null
) : IVideoOutputProvider
{
    /// <inheritdoc/>
    public string Name => "pipewire";

    /// <inheritdoc/>
    public async ValueTask<IVideoOutput> CreateAsync(string name, CancellationToken cancellationToken)
    {
        (PipeWireContext context, _) = await connection.GetAsync(cancellationToken).ConfigureAwait(false);

        // Left for the consumer to link rather than routed by the session manager.
        PipeWireVideoSink sink = new(
            context,
            name,
            new PipeWireVideoSinkOptions { AutoConnect = false },
            loggerFactory
        );
        return new VideoOutput(sink, Name, name);
    }
}

/// <summary>The default PipeWire microphone, and what the default output plays.</summary>
/// <param name="connection">The shared daemon connection.</param>
/// <param name="loggerFactory">Where inputs log.</param>
public sealed class PipeWireAudioInputProvider(
    PipeWireConnection connection,
    ILoggerFactory? loggerFactory = null
) : IAudioInputProvider
{
    private const string Microphone = "default";
    private const string Output = "output";

    /// <inheritdoc/>
    public string Name => "pipewire";

    /// <inheritdoc/>
    public int Rank => 50;

    /// <inheritdoc/>
    public ValueTask<ImmutableArray<AudioInputInfo>> GetInputsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<ImmutableArray<AudioInputInfo>>(
            OperatingSystem.IsLinux()
                ?
                [
                    new AudioInputInfo(Name, Microphone, "Default input", MediaInputKind.Microphone, IsDefault: true),
                    new AudioInputInfo(Name, Output, "Default output", MediaInputKind.Loopback, IsDefault: true),
                ]
                : []
        );

    /// <inheritdoc/>
    public async ValueTask<IAudioInput> OpenAsync(AudioInputInfo input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        (PipeWireContext context, _) = await connection.GetAsync(cancellationToken).ConfigureAwait(false);
        return new AudioInput(
            new PipeWireAudioSource(
                context,
                new PipeWireAudioOptions { CaptureOutput = input.Id == Output },
                loggerFactory
            ),
            input
        );
    }
}

/// <summary>Received audio played on the default PipeWire output.</summary>
/// <param name="connection">The shared daemon connection.</param>
public sealed class PipeWireAudioOutputProvider(PipeWireConnection connection) : IAudioOutputProvider
{
    /// <inheritdoc/>
    public string Name => "pipewire";

    /// <inheritdoc/>
    public async ValueTask<IAudioOutput> CreateAsync(string? name, CancellationToken cancellationToken)
    {
        (PipeWireContext context, _) = await connection.GetAsync(cancellationToken).ConfigureAwait(false);
        PipeWireAudioSink sink = new(
            context,
            name is null ? null : new PipeWireAudioOptions { NodeName = name }
        );
        return new AudioOutput(sink, Name, name ?? "Default output");
    }
}
