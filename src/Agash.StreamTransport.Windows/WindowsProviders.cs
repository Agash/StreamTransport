using System.Collections.Immutable;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.Windows.Spout;
using Agash.StreamTransport.Windows.Wasapi;
using Microsoft.Extensions.Logging;
using Spout2.NET;

namespace Agash.StreamTransport.Windows;

/// <summary>Spout senders as inputs: OBS, VTube Studio, TouchDesigner and anything else sharing over Spout.</summary>
/// <param name="loggerFactory">Where inputs log.</param>
public sealed class SpoutVideoInputProvider(ILoggerFactory? loggerFactory = null) : IVideoInputProvider
{
    /// <inheritdoc/>
    public string Name => "spout";

    /// <inheritdoc/>
    public int Rank => 50;

    /// <inheritdoc/>
    public ValueTask<ImmutableArray<VideoInputInfo>> GetInputsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<ImmutableArray<VideoInputInfo>>(
            [
                .. SpoutSenders
                    .GetAll()
                    .Select(s => new VideoInputInfo(Name, s.Name, s.Name, MediaInputKind.Application, [])),
            ]
        );

    /// <inheritdoc/>
    public ValueTask<IVideoInput> OpenAsync(
        VideoInputInfo input,
        VideoInputRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        return ValueTask.FromResult<IVideoInput>(
            new VideoInput(
                new SpoutVideoSource(new SpoutVideoSourceOptions { SenderName = input.Id }, loggerFactory),
                input
            )
        );
    }
}

/// <summary>Received video published as a Spout sender.</summary>
/// <param name="loggerFactory">Where outputs log.</param>
public sealed class SpoutVideoOutputProvider(ILoggerFactory? loggerFactory = null) : IVideoOutputProvider
{
    /// <inheritdoc/>
    public string Name => "spout";

    /// <inheritdoc/>
    public ValueTask<IVideoOutput> CreateAsync(string name, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IVideoOutput>(
            new VideoOutput(new SpoutVideoSink(name, loggerFactory: loggerFactory), Name, name)
        );
}

/// <summary>The default WASAPI capture device, and what the default output plays.</summary>
/// <param name="loggerFactory">Where inputs log.</param>
public sealed class WasapiAudioInputProvider(ILoggerFactory? loggerFactory = null) : IAudioInputProvider
{
    private const string Microphone = "default";
    private const string Output = "output";

    /// <inheritdoc/>
    public string Name => "wasapi";

    /// <inheritdoc/>
    public int Rank => 100;

    /// <inheritdoc/>
    public ValueTask<ImmutableArray<AudioInputInfo>> GetInputsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<ImmutableArray<AudioInputInfo>>(
            [
                new AudioInputInfo(Name, Microphone, "Default input", MediaInputKind.Microphone, IsDefault: true),
                new AudioInputInfo(Name, Output, "Default output", MediaInputKind.Loopback, IsDefault: true),
            ]
        );

    /// <inheritdoc/>
    public ValueTask<IAudioInput> OpenAsync(AudioInputInfo input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ValueTask.FromResult<IAudioInput>(
            new AudioInput(
                new WasapiAudioSource(
                    input.Id == Output ? WasapiEndpoint.DefaultOutputLoopback : WasapiEndpoint.DefaultCapture,
                    loggerFactory
                ),
                input
            )
        );
    }
}

/// <summary>Received audio played on the default WASAPI output.</summary>
/// <param name="loggerFactory">Where outputs log.</param>
public sealed class WasapiAudioOutputProvider(ILoggerFactory? loggerFactory = null) : IAudioOutputProvider
{
    /// <inheritdoc/>
    public string Name => "wasapi";

    /// <inheritdoc/>
    public ValueTask<IAudioOutput> CreateAsync(string? name, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IAudioOutput>(
            new AudioOutput(new WasapiAudioSink(loggerFactory), Name, name ?? "Default output")
        );
}
