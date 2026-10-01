using Agash.StreamTransport;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.TestSignal;

namespace StreamTransport.Cli;

/// <summary>
/// The inputs or outputs a command line names, opened through <see cref="MediaDevices"/>: whatever the
/// registered providers offer, the platform's own (Spout, Syphon, PipeWire, cameras, audio devices) and
/// the test signal. Owns them.
/// </summary>
internal sealed class Endpoints : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    private Endpoints() { }

    public MediaEndpoints Media { get; private set; } = new();

    /// <summary>The measurement of a received test signal, when the command asked for one.</summary>
    public TestSignalAnalyzer? Analyzer { get; private set; }

    public static async Task<Endpoints> CreateAsync(
        CommandLine command,
        MediaDevices devices,
        CancellationToken cancellationToken
    )
    {
        Endpoints endpoints = new();
        try
        {
            if (command.Publish)
            {
                endpoints.Media = new MediaEndpoints
                {
                    VideoSource = command.Video is { } video
                        ? endpoints.Own(
                            await devices.OpenVideoInputAsync(
                                video,
                                command.Capture,
                                cancellationToken
                            )
                        )
                        : null,
                    AudioSource = command.Audio is { } audio
                        ? endpoints.Own(await devices.OpenAudioInputAsync(audio, cancellationToken))
                        : null,
                };
            }
            else
            {
                TestSignalAnalyzer? analyzer = command.Measure ? new TestSignalAnalyzer() : null;
                endpoints.Analyzer = analyzer;
                IVideoSink? videoSink = null;
                if (analyzer is not null)
                {
                    // Measuring reads the pictures in memory, so the video goes to the analyzer alone.
                    videoSink = analyzer.WrapVideo();
                }
                else if (command.Video is { } video)
                {
                    (string provider, string? name) = Split(video);
                    videoSink = endpoints.Own(
                        await devices.CreateVideoOutputAsync(
                            provider,
                            name ?? $"StreamTransport {command.Room}",
                            cancellationToken
                        )
                    );
                }

                IAudioSink? audioSink = command.Audio is null
                    ? null
                    : endpoints.Own(
                        await devices.CreateAudioOutputAsync(null, null, cancellationToken)
                    );
                endpoints.Media = new MediaEndpoints
                {
                    VideoSink = videoSink,
                    AudioSink = analyzer is not null ? analyzer.WrapAudio(audioSink) : audioSink,
                };
            }

            return endpoints;
        }
        catch
        {
            endpoints.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (IDisposable owned in Enumerable.Reverse(_owned))
        {
            owned.Dispose();
        }
    }

    private T Own<T>(T endpoint)
        where T : IDisposable
    {
        _owned.Add(endpoint);
        return endpoint;
    }

    private static (string Provider, string? Name) Split(string spec)
    {
        int colon = spec.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? (spec, null) : (spec[..colon], spec[(colon + 1)..]);
    }
}
