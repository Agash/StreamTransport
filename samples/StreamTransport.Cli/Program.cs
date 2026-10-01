using Agash.StreamTransport;
using Agash.StreamTransport.Codecs.FFmpeg;
using Agash.StreamTransport.Codecs.Opus;
using Agash.StreamTransport.Linux;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.Signaling;
using Agash.StreamTransport.TestSignal;
using Agash.StreamTransport.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamTransport.Cli;
#if MACOS
using Agash.StreamTransport.MacOS;
using Syphon.NET;
#endif

// streamtransport: publish this machine's shared video and audio to a relay room, or receive a room's
// stream into the platform's own sharing (Spout, Syphon, PipeWire), for OBS and the like to pick up.

CommandLine command;
try
{
    command = CommandLine.Parse(args);
}
catch (FormatException error)
{
    Console.Error.WriteLine(error.Message);
    Console.Error.WriteLine(CommandLine.Usage);
    return 2;
}

#if MACOS
// Syphon finds servers through the main run loop, which a console host hands over while it works.
return SyphonMainLoop.Run(() => RunAsync(command));
#else
return await RunAsync(command);
#endif

static async Task<int> RunAsync(CommandLine command)
{
    using ILoggerFactory loggers = LoggerFactory.Create(builder =>
        builder
            .AddSimpleConsole(options => options.SingleLine = true)
            .SetMinimumLevel(command.Verbose ? LogLevel.Debug : LogLevel.Information)
    );
    ILogger log = loggers.CreateLogger("streamtransport");

    if (command.FFmpeg is not null)
    {
        FFmpeg.Interop.FFmpegLibraries.SearchDirectory = command.FFmpeg;
    }

    ServiceCollection services = new();
    services.AddSingleton(loggers);
    services.AddStreamTransport().AddFFmpegCodecs().AddOpusCodecs();
    if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
    {
        services.AddWindowsMedia();
    }
    else if (OperatingSystem.IsLinux())
    {
        services.AddLinuxMedia();
    }
#if MACOS
    services.AddMacOSMedia();
#endif

    using CancellationTokenSource stop = new();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Cancel();
    };

    await using ServiceProvider provider = services.BuildServiceProvider();
    MediaDevices devices = provider.GetRequiredService<MediaDevices>();
    if (command.List)
    {
        foreach (VideoInputInfo input in await devices.GetVideoInputsAsync(stop.Token))
        {
            string modes = input.Modes.IsEmpty
                ? string.Empty
                : $", {input.Modes.Length} modes, largest {input.Modes.MaxBy(static m => (long)m.Size.Width * m.Size.Height)}";
            Console.WriteLine($"video  {input.Provider}:{input.Id}  {input.Name} ({input.Kind}){modes}");
        }

        foreach (AudioInputInfo input in await devices.GetAudioInputsAsync(stop.Token))
        {
            Console.WriteLine($"audio  {input.Provider}:{input.Id}  {input.Name} ({input.Kind})");
        }

        Console.WriteLine($"video outputs: {string.Join(", ", devices.VideoOutputProviders)}");
        Console.WriteLine($"audio outputs: {string.Join(", ", devices.AudioOutputProviders)}");
        return 0;
    }

    if (command.FeedOutput is { } target)
    {
        int colon = target.IndexOf(':', StringComparison.Ordinal);
        using IVideoInput input = await devices.OpenVideoInputAsync(command.Video!, command.Capture, stop.Token);
        using IVideoOutput output = await devices.CreateVideoOutputAsync(
            colon < 0 ? target : target[..colon],
            colon < 0 ? "StreamTransport" : target[(colon + 1)..],
            stop.Token
        );
        using (input.Connect(output, output.Constraints))
        {
            log.LogInformation("Feeding {Input} to {Output}; Ctrl+C stops.", input.Info.Name, output.Name);
            TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (stop.Token.Register(() => stopped.TrySetResult()))
            {
                await stopped.Task;
            }
        }

        return 0;
    }

    IMediaSessionFactory sessions = provider.GetRequiredService<IMediaSessionFactory>();
    using Endpoints endpoints = await Endpoints.CreateAsync(command, devices, stop.Token);
    PeerRole role = command.Publish ? PeerRole.Publisher : PeerRole.Subscriber;
    await using RoomClient room = await RoomClient.ConnectAsync(
        command.Relay,
        new RoomCode(command.Room),
        role,
        stop.Token
    );

    log.LogInformation(
        "{Role} in room {Room} through {Relay}; Ctrl+C stops.",
        role,
        command.Room,
        command.Relay
    );
    if (command.Publish)
    {
        await using MediaPublisher publisher = new(
            room,
            sessions,
            endpoints.Media,
            command.Session,
            loggers.CreateLogger<MediaPublisher>()
        );
        publisher.Start();
        await ReportAsync(() => publisher.Sessions.Select(s => s.Session), log, stop.Token);
    }
    else
    {
        await using MediaSubscriber subscriber = new(
            room,
            sessions,
            endpoints.Media,
            command.Session,
            loggers.CreateLogger<MediaSubscriber>()
        );
        subscriber.Start();
        await ReportAsync(
            () => subscriber.Session is { } s ? [s] : [],
            log,
            stop.Token,
            endpoints.Analyzer
        );
    }

    return 0;
}

// Every few seconds until Ctrl+C: each session's path and what it has sent and received.
static async Task ReportAsync(
    Func<IEnumerable<IMediaSession>> sessions,
    ILogger log,
    CancellationToken stop,
    TestSignalAnalyzer? analyzer = null
)
{
    using PeriodicTimer every = new(TimeSpan.FromSeconds(5));
    try
    {
        while (await every.WaitForNextTickAsync(stop))
        {
            foreach (IMediaSession session in sessions())
            {
                MediaSessionStatistics counters = session.Statistics;
                log.LogInformation(
                    "{State} over {Route}: sent {VideoSent} video, {AudioSent} audio; decoded {VideoDecoded} video, {AudioDecoded} audio; {Bitrate} kbit/s target.",
                    session.State,
                    session.Route is { } route ? $"{route.Local} -> {route.Remote}" : "no path yet",
                    counters.VideoFramesSent,
                    counters.AudioFramesSent,
                    counters.VideoFramesDecoded,
                    counters.AudioFramesDecoded,
                    session.Health.TargetBitrateBps / 1000
                );
            }

            if (analyzer is not null)
            {
                log.LogInformation("Test signal: {Measurement}.", analyzer.Measure());
            }
        }
    }
    catch (OperationCanceledException)
    {
        // Deliberately not logged: Ctrl+C is how the command ends.
    }
}
