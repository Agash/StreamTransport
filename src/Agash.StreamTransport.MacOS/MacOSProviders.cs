using System.Collections.Immutable;
using Agash.StreamTransport.MacOS.Audio;
using Agash.StreamTransport.MacOS.Syphon;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using Syphon.NET;

namespace Agash.StreamTransport.MacOS;

/// <summary>
/// Syphon servers as inputs: OBS, VTube Studio, Resolume and anything else sharing over Syphon. The
/// directory of servers lives as long as the provider; its announcements arrive on the main run loop,
/// which an AppKit application serves and a console host serves with <see cref="SyphonMainLoop"/>.
/// </summary>
/// <param name="loggerFactory">Where inputs log.</param>
public sealed class SyphonVideoInputProvider(ILoggerFactory? loggerFactory = null)
    : IVideoInputProvider,
        IDisposable
{
    private readonly Lazy<SyphonServerDirectory> _directory = new(() =>
        new SyphonServerDirectory(loggerFactory)
    );

    /// <inheritdoc/>
    public string Name => "syphon";

    /// <inheritdoc/>
    public int Rank => 50;

    /// <inheritdoc/>
    public ValueTask<ImmutableArray<VideoInputInfo>> GetInputsAsync(
        CancellationToken cancellationToken
    ) =>
        ValueTask.FromResult<ImmutableArray<VideoInputInfo>>([
            .. _directory.Value.Servers.Select(s => new VideoInputInfo(
                Name,
                s.Uuid,
                Title(s),
                MediaInputKind.Application,
                []
            )),
        ]);

    /// <inheritdoc/>
    public async ValueTask<IVideoInput> OpenAsync(
        VideoInputInfo input,
        VideoInputRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        SyphonServerDescription server = await _directory
            .Value.WaitForServerAsync(s => s.Uuid == input.Id, cancellationToken)
            .ConfigureAwait(false);
        return new VideoInput(new SyphonVideoSource(server, loggerFactory: loggerFactory), input);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_directory.IsValueCreated)
        {
            _directory.Value.Dispose();
        }
    }

    // What people see: the server's name with its application's, or the application's alone.
    private static string Title(SyphonServerDescription server) =>
        server.Name.Length == 0 ? server.AppName
        : server.AppName.Length == 0 ? server.Name
        : $"{server.AppName} - {server.Name}";
}

/// <summary>Received video published as a Syphon server.</summary>
/// <param name="loggerFactory">Where outputs log.</param>
public sealed class SyphonVideoOutputProvider(ILoggerFactory? loggerFactory = null)
    : IVideoOutputProvider
{
    /// <inheritdoc/>
    public string Name => "syphon";

    /// <inheritdoc/>
    public ValueTask<IVideoOutput> CreateAsync(string name, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IVideoOutput>(
            new VideoOutput(new SyphonVideoSink(name, loggerFactory: loggerFactory), Name, name)
        );
}

/// <summary>The default Core Audio input.</summary>
/// <param name="loggerFactory">Where inputs log.</param>
public sealed class CoreAudioInputProvider(ILoggerFactory? loggerFactory = null)
    : IAudioInputProvider
{
    /// <inheritdoc/>
    public string Name => "coreaudio";

    /// <inheritdoc/>
    public int Rank => 100;

    /// <inheritdoc/>
    public ValueTask<ImmutableArray<AudioInputInfo>> GetInputsAsync(
        CancellationToken cancellationToken
    ) =>
        ValueTask.FromResult<ImmutableArray<AudioInputInfo>>([
            new AudioInputInfo(
                Name,
                "default",
                "Default input",
                MediaInputKind.Microphone,
                IsDefault: true
            ),
        ]);

    /// <inheritdoc/>
    public ValueTask<IAudioInput> OpenAsync(
        AudioInputInfo input,
        CancellationToken cancellationToken
    ) =>
        ValueTask.FromResult<IAudioInput>(
            new AudioInput(new CoreAudioSource(loggerFactory), input)
        );
}

/// <summary>Received audio played on the default Core Audio output.</summary>
/// <param name="loggerFactory">Where outputs log.</param>
public sealed class CoreAudioOutputProvider(ILoggerFactory? loggerFactory = null)
    : IAudioOutputProvider
{
    /// <inheritdoc/>
    public string Name => "coreaudio";

    /// <inheritdoc/>
    public ValueTask<IAudioOutput> CreateAsync(string? name, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IAudioOutput>(
            new AudioOutput(new CoreAudioSink(loggerFactory), Name, name ?? "Default output")
        );
}
