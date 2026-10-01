using Agash.StreamTransport;
using Agash.StreamTransport.Codecs.FFmpeg;
using Agash.StreamTransport.Codecs.Opus;
using Agash.StreamTransport.Linux;
using Agash.StreamTransport.Signaling;
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
    IMediaSessionFactory sessions = provider.GetRequiredService<IMediaSessionFactory>();
    await using Endpoints endpoints = await Endpoints.CreateAsync(command, loggers, stop.Token);
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
        await ReportAsync(() => subscriber.Session is { } s ? [s] : [], log, stop.Token);
    }

    return 0;
}

// Every few seconds until Ctrl+C: each session's path and what it has sent and received.
static async Task ReportAsync(
    Func<IEnumerable<IMediaSession>> sessions,
    ILogger log,
    CancellationToken stop
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
        }
    }
    catch (OperationCanceledException)
    {
        // Deliberately not logged: Ctrl+C is how the command ends.
    }
}
