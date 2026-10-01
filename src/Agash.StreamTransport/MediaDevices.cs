using System.Collections.Immutable;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport;

/// <summary>
/// Every input and output the registered providers offer: cameras, shared application outputs, audio
/// devices, and whatever a host adds (NDI, a game capture) by registering its own providers. Inputs are
/// named by spec, <c>provider:name</c> or just <c>name</c>. An input more than one provider reaches (a
/// camera through its native API and through FFmpeg) is listed once, by its best provider, and opening it
/// falls back to the next provider that has it when the first fails.
/// </summary>
public sealed partial class MediaDevices
{
    private readonly ImmutableArray<IVideoInputProvider> _videoInputs;
    private readonly ImmutableArray<IAudioInputProvider> _audioInputs;
    private readonly ImmutableArray<IVideoOutputProvider> _videoOutputs;
    private readonly ImmutableArray<IAudioOutputProvider> _audioOutputs;
    private readonly ILogger _logger;

    /// <summary>The devices of the given providers.</summary>
    /// <param name="videoInputs">Video input providers.</param>
    /// <param name="audioInputs">Audio input providers.</param>
    /// <param name="videoOutputs">Video output providers.</param>
    /// <param name="audioOutputs">Audio output providers.</param>
    /// <param name="logger">The logger.</param>
    public MediaDevices(
        IEnumerable<IVideoInputProvider> videoInputs,
        IEnumerable<IAudioInputProvider> audioInputs,
        IEnumerable<IVideoOutputProvider> videoOutputs,
        IEnumerable<IAudioOutputProvider> audioOutputs,
        ILogger<MediaDevices>? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(videoInputs);
        ArgumentNullException.ThrowIfNull(audioInputs);
        ArgumentNullException.ThrowIfNull(videoOutputs);
        ArgumentNullException.ThrowIfNull(audioOutputs);
        _videoInputs = [.. videoInputs.OrderByDescending(static p => p.Rank)];
        _audioInputs = [.. audioInputs.OrderByDescending(static p => p.Rank)];
        _videoOutputs = [.. videoOutputs];
        _audioOutputs = [.. audioOutputs];
        _logger = logger ?? NullLogger<MediaDevices>.Instance;
    }

    /// <summary>The names of the registered video output providers.</summary>
    public ImmutableArray<string> VideoOutputProviders => [.. _videoOutputs.Select(static p => p.Name)];

    /// <summary>The names of the registered audio output providers.</summary>
    public ImmutableArray<string> AudioOutputProviders => [.. _audioOutputs.Select(static p => p.Name)];

    /// <summary>The video inputs present now, each by its best provider.</summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The inputs.</returns>
    public async Task<ImmutableArray<VideoInputInfo>> GetVideoInputsAsync(
        CancellationToken cancellationToken = default
    )
    {
        List<VideoInputInfo> inputs = [];
        foreach (IVideoInputProvider provider in _videoInputs)
        {
            foreach (VideoInputInfo input in await EnumerateAsync(provider, cancellationToken).ConfigureAwait(false))
            {
                if (!inputs.Exists(i => Same(i.Kind, i.Name, input.Kind, input.Name)))
                {
                    inputs.Add(input);
                }
            }
        }

        return [.. inputs];
    }

    /// <summary>The audio inputs present now, each by its best provider.</summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The inputs.</returns>
    public async Task<ImmutableArray<AudioInputInfo>> GetAudioInputsAsync(
        CancellationToken cancellationToken = default
    )
    {
        List<AudioInputInfo> inputs = [];
        foreach (IAudioInputProvider provider in _audioInputs)
        {
            foreach (AudioInputInfo input in await EnumerateAsync(provider, cancellationToken).ConfigureAwait(false))
            {
                if (!inputs.Exists(i => Same(i.Kind, i.Name, input.Kind, input.Name)))
                {
                    inputs.Add(input);
                }
            }
        }

        return [.. inputs];
    }

    /// <summary>
    /// Opens a video input by spec: <c>provider:name</c>, <c>provider</c> for its first input, <c>name</c>
    /// for the first input of that name or id, or a kind (<c>camera</c>, <c>application</c>, ...) for the
    /// first input of it. An empty name part matches the provider's first input.
    /// </summary>
    /// <param name="spec">Which input.</param>
    /// <param name="request">What to capture; the input's best mode when null.</param>
    /// <param name="cancellationToken">Cancels opening.</param>
    /// <returns>The open input.</returns>
    /// <exception cref="InvalidOperationException">No provider has the input, or every one that has it failed.</exception>
    public async Task<IVideoInput> OpenVideoInputAsync(
        string spec,
        VideoInputRequest? request = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(spec);
        request ??= new VideoInputRequest();
        (string? providerName, string? name) = Split(spec, _videoInputs.Select(static p => p.Name));
        MediaInputKind? kind = providerName is null ? Kind(name) : null;
        List<Exception> failures = [];
        VideoInputInfo? chosen = null;
        foreach (IVideoInputProvider provider in _videoInputs)
        {
            if (providerName is not null && !Is(provider.Name, providerName) && chosen is null)
            {
                continue;
            }

            ImmutableArray<VideoInputInfo> inputs = await EnumerateAsync(provider, cancellationToken)
                .ConfigureAwait(false);
            VideoInputInfo? input = chosen is { } first
                // A fallback provider must have the same input, by kind and name.
                ? inputs.FirstOrDefault(i => Same(i.Kind, i.Name, first.Kind, first.Name))
                : inputs.FirstOrDefault(i => Matches(i.Id, i.Name, i.Kind, name, kind));
            if (input is null)
            {
                continue;
            }

            chosen ??= input;
            try
            {
                IVideoInput opened = await provider.OpenAsync(input, request, cancellationToken).ConfigureAwait(false);
                LogOpened(input.Name, provider.Name, opened.Mode?.ToString() ?? "the producer's format");
                return opened;
            }
            catch (Exception exception) when (IsOpenFailure(exception))
            {
                LogOpenFailed(exception, input.Name, provider.Name);
                failures.Add(exception);
            }
        }

        throw chosen is null
            ? new InvalidOperationException($"There is no video input '{spec}'.")
            : new InvalidOperationException(
                $"No provider could open the video input '{spec}'.",
                new AggregateException(failures)
            );
    }

    /// <summary>Opens an audio input by spec, as <see cref="OpenVideoInputAsync"/> names video inputs.</summary>
    /// <param name="spec">Which input; <c>default</c> for the default microphone.</param>
    /// <param name="cancellationToken">Cancels opening.</param>
    /// <returns>The open input.</returns>
    /// <exception cref="InvalidOperationException">No provider has the input, or every one that has it failed.</exception>
    public async Task<IAudioInput> OpenAudioInputAsync(
        string spec,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(spec);
        (string? providerName, string? name) = Split(spec, _audioInputs.Select(static p => p.Name));
        bool wantsDefault = name is null || Is(name, "default");
        MediaInputKind? kind = wantsDefault ? MediaInputKind.Microphone : providerName is null ? Kind(name) : null;
        List<Exception> failures = [];
        AudioInputInfo? chosen = null;
        foreach (IAudioInputProvider provider in _audioInputs)
        {
            if (providerName is not null && !Is(provider.Name, providerName) && chosen is null)
            {
                continue;
            }

            ImmutableArray<AudioInputInfo> inputs = await EnumerateAsync(provider, cancellationToken)
                .ConfigureAwait(false);
            AudioInputInfo? input = chosen is { } first
                ? inputs.FirstOrDefault(i => Same(i.Kind, i.Name, first.Kind, first.Name))
                : wantsDefault
                    ? inputs.FirstOrDefault(i => i.Kind == kind && i.IsDefault)
                        ?? inputs.FirstOrDefault(i => i.Kind == kind)
                    : inputs.FirstOrDefault(i => Matches(i.Id, i.Name, i.Kind, name, kind));
            if (input is null)
            {
                continue;
            }

            chosen ??= input;
            try
            {
                IAudioInput opened = await provider.OpenAsync(input, cancellationToken).ConfigureAwait(false);
                LogOpened(input.Name, provider.Name, opened.Format.ToString());
                return opened;
            }
            catch (Exception exception) when (IsOpenFailure(exception))
            {
                LogOpenFailed(exception, input.Name, provider.Name);
                failures.Add(exception);
            }
        }

        throw chosen is null
            ? new InvalidOperationException($"There is no audio input '{spec}'.")
            : new InvalidOperationException(
                $"No provider could open the audio input '{spec}'.",
                new AggregateException(failures)
            );
    }

    /// <summary>Publishes a video output through the named provider.</summary>
    /// <param name="provider">The provider, such as <c>spout</c>; null for the first registered.</param>
    /// <param name="name">The name other software sees.</param>
    /// <param name="cancellationToken">Cancels creation.</param>
    /// <returns>The output.</returns>
    /// <exception cref="InvalidOperationException">No such provider is registered.</exception>
    public async Task<IVideoOutput> CreateVideoOutputAsync(
        string? provider,
        string name,
        CancellationToken cancellationToken = default
    )
    {
        IVideoOutputProvider chosen =
            _videoOutputs.FirstOrDefault(p => provider is null || Is(p.Name, provider))
            ?? throw new InvalidOperationException($"There is no video output provider '{provider}'.");
        return await chosen.CreateAsync(name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Makes an audio output through the named provider.</summary>
    /// <param name="provider">The provider; null for the first registered.</param>
    /// <param name="name">The device or published name; null for the default device.</param>
    /// <param name="cancellationToken">Cancels creation.</param>
    /// <returns>The output.</returns>
    /// <exception cref="InvalidOperationException">No such provider is registered.</exception>
    public async Task<IAudioOutput> CreateAudioOutputAsync(
        string? provider,
        string? name = null,
        CancellationToken cancellationToken = default
    )
    {
        IAudioOutputProvider chosen =
            _audioOutputs.FirstOrDefault(p => provider is null || Is(p.Name, provider))
            ?? throw new InvalidOperationException($"There is no audio output provider '{provider}'.");
        return await chosen.CreateAsync(name, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ImmutableArray<VideoInputInfo>> EnumerateAsync(
        IVideoInputProvider provider,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await provider.GetInputsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsEnumerationFailure(exception))
        {
            LogEnumerationFailed(exception, provider.Name);
            return [];
        }
    }

    private async ValueTask<ImmutableArray<AudioInputInfo>> EnumerateAsync(
        IAudioInputProvider provider,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await provider.GetInputsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsEnumerationFailure(exception))
        {
            LogEnumerationFailed(exception, provider.Name);
            return [];
        }
    }

    // provider:name, provider, or name; a leading part is a provider only when one is registered by it.
    private static (string? Provider, string? Name) Split(string spec, IEnumerable<string> providers)
    {
        int colon = spec.IndexOf(':', StringComparison.Ordinal);
        string head = colon < 0 ? spec : spec[..colon];
        if (providers.Any(p => Is(p, head)))
        {
            string? rest = colon < 0 ? null : spec[(colon + 1)..];
            return (head, string.IsNullOrEmpty(rest) ? null : rest);
        }

        return (null, spec);
    }

    private static MediaInputKind? Kind(string? name) =>
        Enum.TryParse(name, ignoreCase: true, out MediaInputKind kind) && !int.TryParse(name, out _)
            ? kind
            : null;

    private static bool Matches(string id, string inputName, MediaInputKind inputKind, string? name, MediaInputKind? kind) =>
        name is null
        || (kind is { } k && inputKind == k)
        || string.Equals(id, name, StringComparison.Ordinal)
        || string.Equals(inputName, name, StringComparison.OrdinalIgnoreCase);

    private static bool Same(MediaInputKind a, string aName, MediaInputKind b, string bName) =>
        a == b && string.Equals(aName, bName, StringComparison.OrdinalIgnoreCase);

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsOpenFailure(Exception exception) =>
        exception
            is IOException
                or InvalidOperationException
                or NotSupportedException
                or UnauthorizedAccessException
                or PlatformNotSupportedException
                or TimeoutException;

    private static bool IsEnumerationFailure(Exception exception) =>
        IsOpenFailure(exception) || exception is DllNotFoundException or EntryPointNotFoundException;

    [LoggerMessage(2900, LogLevel.Information, "Opened {Input} through {Provider}: {Mode}.")]
    private partial void LogOpened(string input, string provider, string mode);

    [LoggerMessage(
        2901,
        LogLevel.Warning,
        "{Provider} could not open {Input}; trying the next provider that has it."
    )]
    private partial void LogOpenFailed(Exception exception, string input, string provider);

    [LoggerMessage(2902, LogLevel.Warning, "{Provider} could not list its inputs.")]
    private partial void LogEnumerationFailed(Exception exception, string provider);
}
