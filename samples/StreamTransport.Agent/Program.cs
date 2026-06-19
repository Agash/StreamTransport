using Agash.StreamTransport;
using Agash.StreamTransport.Codecs;
using Agash.StreamTransport.DependencyInjection;
using Agash.StreamTransport.WebRtc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamTransport.Agent;
using Spectre.Console;

// StreamTransport.Agent - the cross-platform send/receive proof-of-concept for Agash.StreamTransport.
// It joins a relay room and either publishes a stream (synthetic test pattern, or a real camera + mic
// captured through FFmpeg libavdevice) or subscribes and decodes one. Audio and video are stamped from a
// common clock, so WebRTC lip-syncs them. Run two instances (or two machines) pointed at the same relay
// for an end-to-end test; the same camera path drives the linux-arm64 rockchip IRL field agent.

AnsiConsole.Write(new FigletText("StreamTransport").Color(Color.Teal));
AnsiConsole.MarkupLine("[grey]Local-first WebRTC media agent[/]\n");

if (args.Length > 0 && args[0].Equals("selftest", StringComparison.OrdinalIgnoreCase))
{
    // `selftest alpha [encoder]` runs the GPU side-by-side-alpha round-trip (pack -> encode -> decode ->
    // unpack) through a real hardware encoder, with no Spout sender / OBS needed.
    // `selftest caps` probes and prints the host's usable HEVC hardware encode/decode capabilities. Works on
    // every platform (no relay, no GPU surface), so it doubles as a quick "what will this machine do?" check.
    if (args.Length > 1 && args[1].Equals("caps", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine(Agash.StreamTransport.Codecs.CodecCapabilities.Probe().Describe());
        return 0;
    }

    bool alpha = args.Length > 1 && args[1].Equals("alpha", StringComparison.OrdinalIgnoreCase);
    string selfTestEncoder = args.Length > 2 ? args[2] : "hevc_nvenc";
#if HAS_PIPEWIRE
    // `selftest pwdmabuf` verifies the Linux GPU zero-copy publish (VAAPI presentation pool -> PipeWire dmabuf
    // producer -> PipeWire consumer) in-process, independent of GStreamer's (stack-dependent) dmabuf support.
    if (args.Length > 1 && args[1].Equals("pwdmabuf", StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsLinux())
    {
        return await PwDmaBufSelfTest.RunAsync();
    }
#endif
#if WINDOWS_HEAD
    return alpha ? SpoutSelfTest.RunAlpha(selfTestEncoder) : SpoutSelfTest.Run();
#else
#if HAS_SYPHON
    if (OperatingSystem.IsMacOS())
    {
        return SyphonSelfTest.Run();
    }
#endif
    AnsiConsole.MarkupLine("[yellow]selftest is available on macOS (Syphon) and Windows (Spout) only.[/]");
    return 1;
#endif
}

AgentConfig config = AgentConfig.FromArgs(args) ?? Interactive.Prompt();

// Compose the agent as a Microsoft.Extensions hosted application: the transport's services (codec engine,
// DTLS-SRTP, congestion control, peer-connection + transport factories) come from DI, and structured logging
// is routed through Spectre.Console so the library's diagnostics share the agent's styled console.
bool verbose = Array.Exists(args, a => string.Equals(a, "--verbose", StringComparison.OrdinalIgnoreCase));

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
builder.Logging.AddProvider(new SpectreLoggerProvider(verbose ? LogLevel.Debug : LogLevel.Information));
builder.Services.AddStreamTransport();
// The platform media factory (capture sources / publish sinks) and the two run loops are DI services, so the
// host ILoggerFactory flows into them by constructor injection - that's how PipeWire stream tracing reaches
// the agent's --verbose without being threaded through call signatures.
builder.Services.AddSingleton<AgentMediaFactory>();
builder.Services.AddSingleton<Publish>();
builder.Services.AddSingleton<Subscribe>();
using IHost host = builder.Build();
MediaSessionFactory transport = host.Services.GetRequiredService<MediaSessionFactory>();

// Network awareness: surface interface changes. The mobility layer will act on these; for now
// the agent just reports them, which also validates the monitor on each platform.
INetworkMonitor networkMonitor = host.Services.GetRequiredService<INetworkMonitor>();
ILogger networkLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Network");
networkMonitor.NetworksChanged += paths => networkLogger.LogInformation(
    "Network changed: {Count} interface(s) up [{Interfaces}]",
    paths.Count,
    string.Join(", ", paths.Select(p => $"{p.Name}/{p.AdapterType}")));
networkMonitor.Start();

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

try
{
    return config.Role == PeerRole.Publisher
        ? await host.Services.GetRequiredService<Publish>().RunAsync(config, transport, shutdown.Token)
        : await host.Services.GetRequiredService<Subscribe>().RunAsync(config, transport, shutdown.Token);
}
catch (OperationCanceledException)
{
    AnsiConsole.MarkupLine("\n[grey]stopped.[/]");
    return 0;
}
catch (Exception ex)
{
    AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
    // Rich exception formatter uses dynamic code (not NativeAOT-safe); plain dump when AOT-compiled.
    if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
    {
        AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
    }
    else
    {
        AnsiConsole.WriteLine(ex.ToString());
    }

    return 1;
}

/// <summary>Resolved run configuration, from CLI args or interactive prompts.</summary>
internal sealed record AgentConfig(
    PeerRole Role,
    Uri? Relay,
    string Room,
    VideoSourceKind Source,
    bool Audio,
    bool Video,
    string? VideoDevice,
    string? AudioDevice,
    string? EncoderName,
    bool Host = false,
    bool DevTunnel = false,
    string? SpoutSender = null,
    string? PublishSpout = null,
    string? PublishSyphon = null,
    string? PublishPipeWire = null,
    string? SyphonServer = null,
    bool Alpha = false,
    bool Verify = false,
    bool Synced = false,
    int Seconds = 15,
    int BFrames = 0,
    MediaProfile Profile = MediaProfile.InteractiveP2P,
    IReadOnlyList<string>? Interfaces = null,
    int Fps = 30,
    int Width = 1280,
    int Height = 720)
{
    public static AgentConfig? FromArgs(string[] args)
    {
        if (args.Length == 0)
        {
            return null; // fall through to interactive.
        }

        PeerRole role = args[0].Equals("send", StringComparison.OrdinalIgnoreCase) ? PeerRole.Publisher
            : args[0].Equals("receive", StringComparison.OrdinalIgnoreCase) ? PeerRole.Subscriber
            : throw new ArgumentException($"first argument must be 'send' or 'receive', got '{args[0]}'.");

        string? relay = null, room = null, videoDevice = null, audioDevice = null, encoder = null, spoutSender = null, publishSpout = null, publishSyphon = null, publishPipeWire = null, syphonServer = null;
        var source = VideoSourceKind.Synthetic;
        bool audio = true, video = true, host = false, devTunnel = false, alpha = false, verify = false, synced = false;
        int seconds = 15;
        int bFrames = 0;
        int fps = 30;
        int width = 1280;
        int height = 720;
        var profile = MediaProfile.InteractiveP2P;
        var interfaces = new List<string>();

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--relay": relay = args[++i]; break;
                case "--room": room = args[++i]; break;
                case "--host": host = true; break;
                case "--devtunnel": host = true; devTunnel = true; break;
                case "--source": source = Enum.Parse<VideoSourceKind>(args[++i], ignoreCase: true); break;
                case "--video-device": videoDevice = args[++i]; source = VideoSourceKind.Camera; break;
                case "--audio-device": audioDevice = args[++i]; break;
                case "--spout-sender": spoutSender = args[++i]; source = VideoSourceKind.Spout; break;
                case "--syphon-server": syphonServer = args[++i]; source = VideoSourceKind.Syphon; break;
                case "--publish-spout": publishSpout = args[++i]; break;
                case "--publish-syphon": publishSyphon = args[++i]; break;
                case "--publish-pipewire": publishPipeWire = args[++i]; break;
                case "--encoder": encoder = args[++i]; break;
                case "--audio-only": video = false; break;
                case "--video-only": audio = false; break;
                case "--alpha": alpha = true; break;
                case "--verify": verify = true; break;
                case "--synced": synced = true; break;
                case "--seconds": seconds = int.Parse(args[++i]); break;
                case "--fps": fps = int.Parse(args[++i]); break;
                case "--resolution":
                {
                    string[] wh = args[++i].Split('x', 'X');
                    if (wh.Length != 2 || !int.TryParse(wh[0], out width) || !int.TryParse(wh[1], out height))
                        throw new ArgumentException("--resolution expects WxH, e.g. 1920x1080.");
                    break;
                }
                case "--bframes": bFrames = int.Parse(args[++i]); break;
                case "--profile": profile = ParseProfile(args[++i]); break;
                case "--interface": interfaces.Add(args[++i]); break;
                case "--verbose": break; // handled before host build; ignore here.
                default: throw new ArgumentException($"unknown option '{args[i]}'.");
            }
        }

        if (host && role != PeerRole.Publisher)
        {
            throw new ArgumentException("--host/--devtunnel is only valid for 'send' (the sender hosts signaling).");
        }

        Uri? relayUri = null;
        if (!host)
        {
            if (relay is null || !Uri.TryCreate(relay, UriKind.Absolute, out relayUri))
            {
                throw new ArgumentException("--relay <ws-url> is required (or use --host / --devtunnel for send).");
            }
        }

        room ??= role == PeerRole.Publisher ? RandomRoom() : throw new ArgumentException("--room <code> is required for receive.");
        return new AgentConfig(
            role, relayUri, room, source, audio, video, videoDevice, audioDevice, encoder, host, devTunnel, spoutSender, publishSpout, publishSyphon, publishPipeWire, syphonServer, alpha, verify, synced, seconds, bFrames, profile, interfaces, fps, width, height);
    }

    private static MediaProfile ParseProfile(string value) => value.ToLowerInvariant() switch
    {
        "interactive" or "p2p" or "avatar" or "interactivep2p" => MediaProfile.InteractiveP2P,
        "screenshare" or "screen" => MediaProfile.ScreenShare,
        "irl" or "irlcontribution" or "field" => MediaProfile.IrlContribution,
        _ => throw new ArgumentException($"unknown profile '{value}' (interactive|screenshare|irl)."),
    };

    public static string RandomRoom() => Guid.NewGuid().ToString("n")[..6];
}

/// <summary>The video source a publisher uses.</summary>
internal enum VideoSourceKind
{
    /// <summary>An animated NV12 test pattern - no capture hardware needed.</summary>
    Synthetic,

    /// <summary>A real camera captured via FFmpeg libavdevice (dshow / AVFoundation / v4l2).</summary>
    Camera,

    /// <summary>A Spout sender's shared GPU texture (Windows), captured zero-copy and encoded directly.</summary>
    Spout,

    /// <summary>A PipeWire video node (Linux desktop/screen/camera).</summary>
    PipeWire,

    /// <summary>A Syphon server's shared IOSurface (macOS), captured zero-copy.</summary>
    Syphon,
}

/// <summary>Interactive Spectre.Console prompts when run without arguments.</summary>
internal static class Interactive
{
    public static AgentConfig Prompt()
    {
        string role = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("What should this agent do?")
                .AddChoices("send (publish a stream)", "receive (subscribe to a stream)"));
        bool publisher = role.StartsWith("send", StringComparison.Ordinal);

        Uri? relay = null;
        bool host = false, devTunnel = false;
        if (publisher)
        {
            string mode = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("How should peers reach you?")
                    .AddChoices(
                        "connect to a relay server (URL)",
                        "host signaling on this machine + DevTunnel (share over the internet)",
                        "host signaling on this machine (LAN only)"));
            if (mode.StartsWith("connect", StringComparison.Ordinal))
            {
                relay = new Uri(AnsiConsole.Prompt(new TextPrompt<string>("Relay WebSocket URL:").DefaultValue("ws://localhost:8080/ws")));
            }
            else
            {
                host = true;
                devTunnel = mode.Contains("DevTunnel", StringComparison.Ordinal);
            }
        }
        else
        {
            relay = new Uri(AnsiConsole.Prompt(new TextPrompt<string>("Relay WebSocket URL:").DefaultValue("ws://localhost:8080/ws")));
        }

        var source = VideoSourceKind.Synthetic;
        string? videoDevice = null;
        string? audioDevice = null;
        if (publisher)
        {
            string choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Video source?")
                    .AddChoices("synthetic test pattern", $"camera ({CaptureBackend.VideoInputFormat})"));
            if (choice.StartsWith("camera", StringComparison.Ordinal))
            {
                source = VideoSourceKind.Camera;
                videoDevice = AnsiConsole.Prompt(new TextPrompt<string>("Camera device:").DefaultValue(DefaultVideoDevice()));
                audioDevice = AnsiConsole.Prompt(new TextPrompt<string>("Microphone device (blank for none):").AllowEmpty());
            }
        }

        string room = publisher
            ? AnsiConsole.Prompt(new TextPrompt<string>("Room code:").DefaultValue(AgentConfig.RandomRoom()))
            : AnsiConsole.Prompt(new TextPrompt<string>("Room code to join:"));

        bool audio = source == VideoSourceKind.Synthetic || !string.IsNullOrWhiteSpace(audioDevice) || !publisher;
        return new AgentConfig(
            publisher ? PeerRole.Publisher : PeerRole.Subscriber,
            relay, room, source, audio, Video: true, videoDevice, audioDevice, EncoderName: null, host, devTunnel);
    }

    private static string DefaultVideoDevice() =>
        OperatingSystem.IsWindows() ? "Integrated Camera" : OperatingSystem.IsMacOS() ? "0" : "/dev/video0";
}
