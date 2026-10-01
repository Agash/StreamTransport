using System.Runtime.Versioning;
using Agash.StreamTransport;
using Agash.StreamTransport.Linux.PipeWire;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.Windows.Spout;
using Agash.StreamTransport.Windows.Wasapi;
using Microsoft.Extensions.Logging;
using PipeWire.NET;
#if MACOS
using Agash.StreamTransport.MacOS.Audio;
using Agash.StreamTransport.MacOS.Syphon;
using Syphon.NET;
#endif

namespace StreamTransport.Cli;

/// <summary>
/// The sources or sinks a command line names, made on the platform's own sharing: Spout and WASAPI on
/// Windows, Syphon and Core Audio on macOS, PipeWire on Linux. Owns them and what they run on.
/// </summary>
internal sealed class Endpoints : IAsyncDisposable
{
    private readonly List<IDisposable> _owned = [];
    private PipeWireContext? _pipeWire;

    private Endpoints() { }

    public MediaEndpoints Media { get; private set; } = new();

    public static async Task<Endpoints> CreateAsync(
        CommandLine command,
        ILoggerFactory loggers,
        CancellationToken cancellationToken
    )
    {
        Endpoints endpoints = new();
        try
        {
            (string Kind, string? Name)? video = command.Video is { } spec ? Split(spec) : null;
            if (command.Publish)
            {
                endpoints.Media = new MediaEndpoints
                {
                    VideoSource = video is { } v
                        ? await endpoints.SourceAsync(
                            v.Kind,
                            v.Name,
                            command,
                            loggers,
                            cancellationToken
                        )
                        : null,
                    AudioSource = command.Audio is null
                        ? null
                        : await endpoints.AudioSourceAsync(
                            command.Audio == "output",
                            loggers,
                            cancellationToken
                        ),
                };
            }
            else
            {
                string name = video?.Name ?? $"StreamTransport {command.Room}";
                endpoints.Media = new MediaEndpoints
                {
                    VideoSink = video is { } v
                        ? await endpoints.SinkAsync(v.Kind, name, loggers, cancellationToken)
                        : null,
                    AudioSink = command.Audio is null
                        ? null
                        : await endpoints.AudioSinkAsync(loggers, cancellationToken),
                };
            }

            return endpoints;
        }
        catch
        {
            await endpoints.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IDisposable owned in Enumerable.Reverse(_owned))
        {
            owned.Dispose();
        }

        if (OperatingSystem.IsLinux() && _pipeWire is not null)
        {
            await _pipeWire.DisposeAsync();
        }
    }

    private async Task<IVideoSource> SourceAsync(
        string kind,
        string? name,
        CommandLine command,
        ILoggerFactory loggers,
        CancellationToken cancellationToken
    )
    {
        switch (kind)
        {
            case "test":
                return Own(new TestPattern(new VideoSize(1280, 720), command.Session.FrameRate));
            case "spout" when OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041):
                return Own(
                    new SpoutVideoSource(new SpoutVideoSourceOptions { SenderName = name }, loggers)
                );
            case "pipewire" when OperatingSystem.IsLinux():
                PipeWireContext context = await PipeWireAsync(loggers, cancellationToken);
                return Own(
                    new PipeWireVideoSource(
                        context,
                        uint.TryParse(name, out uint node)
                            ? new PipeWireVideoSourceOptions { TargetNodeId = node }
                            : new PipeWireVideoSourceOptions { TargetObject = name },
                        loggers
                    )
                );
#if MACOS
            case "syphon":
                using (SyphonServerDirectory directory = new(loggers))
                {
                    SyphonServerDescription server = await directory.WaitForServerAsync(
                        s =>
                            name is null
                            || s.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                            || s.AppName.Contains(name, StringComparison.OrdinalIgnoreCase),
                        cancellationToken
                    );
                    return Own(new SyphonVideoSource(server, loggerFactory: loggers));
                }
#endif
            default:
                throw new FormatException($"'{kind}' is not a video source on this platform.");
        }
    }

    private async Task<IVideoSink> SinkAsync(
        string kind,
        string name,
        ILoggerFactory loggers,
        CancellationToken cancellationToken
    )
    {
        switch (kind)
        {
            case "spout" when OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041):
                return Own(new SpoutVideoSink(name, loggerFactory: loggers));
            case "pipewire" when OperatingSystem.IsLinux():
                // Left for the consumer (OBS) to link, rather than routed by the session manager.
                return Own(
                    new PipeWireVideoSink(
                        await PipeWireAsync(loggers, cancellationToken),
                        name,
                        new PipeWireVideoSinkOptions { AutoConnect = false },
                        loggers
                    )
                );
#if MACOS
            case "syphon":
                return Own(new SyphonVideoSink(name, loggerFactory: loggers));
#endif
            default:
                throw new FormatException($"'{kind}' is not a video sink on this platform.");
        }
    }

    private async Task<IAudioSource> AudioSourceAsync(
        bool output,
        ILoggerFactory loggers,
        CancellationToken cancellationToken
    )
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return Own(
                new WasapiAudioSource(
                    output ? WasapiEndpoint.DefaultOutputLoopback : WasapiEndpoint.DefaultCapture,
                    loggers
                )
            );
        }

        if (OperatingSystem.IsLinux())
        {
            return Own(
                new PipeWireAudioSource(
                    await PipeWireAsync(loggers, cancellationToken),
                    new PipeWireAudioOptions { CaptureOutput = output },
                    loggers
                )
            );
        }

#if MACOS
        return output
            ? throw new FormatException("Capturing the output is not available on macOS.")
            : Own(new CoreAudioSource(loggers));
#else
        throw new PlatformNotSupportedException("No audio input on this platform.");
#endif
    }

    private async Task<IAudioSink> AudioSinkAsync(
        ILoggerFactory loggers,
        CancellationToken cancellationToken
    )
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return Own(new WasapiAudioSink(loggers));
        }

        if (OperatingSystem.IsLinux())
        {
            return Own(new PipeWireAudioSink(await PipeWireAsync(loggers, cancellationToken)));
        }

#if MACOS
        return Own(new CoreAudioSink(loggers));
#else
        throw new PlatformNotSupportedException("No audio output on this platform.");
#endif
    }

    // One connection to the daemon, shared by every PipeWire endpoint.
    [SupportedOSPlatform("linux")]
    private async Task<PipeWireContext> PipeWireAsync(
        ILoggerFactory loggers,
        CancellationToken cancellationToken
    )
    {
        if (_pipeWire is null)
        {
            PipeWireContext context = new("streamtransport", loggers);
            await context.StartAsync(cancellationToken);
            _pipeWire = context;
        }

        return _pipeWire;
    }

    private T Own<T>(T endpoint)
        where T : IDisposable
    {
        _owned.Add(endpoint);
        return endpoint;
    }

    private static (string Kind, string? Name) Split(string spec)
    {
        int colon = spec.IndexOf(':', StringComparison.Ordinal);
        return colon < 0
            ? (spec.ToLowerInvariant(), null)
            : (spec[..colon].ToLowerInvariant(), spec[(colon + 1)..]);
    }
}
