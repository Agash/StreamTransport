using System.Globalization;
using System.Runtime.InteropServices;
using Agash.StreamTransport;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.WebRtc.Ice;
using Agash.StreamTransport.WebRtc.Transport;

namespace StreamTransport.Cli;

/// <summary>What the command line asks for.</summary>
/// <param name="List">List the inputs and outputs and stop.</param>
/// <param name="Publish">Publish when true, subscribe when false.</param>
/// <param name="Relay">The relay's WebSocket URL.</param>
/// <param name="Room">The room to join.</param>
/// <param name="Video">The video endpoint spec, or null for none.</param>
/// <param name="Audio">The audio endpoint spec, or null for none.</param>
/// <param name="Session">How the session is set up.</param>
/// <param name="Verbose">Whether to log at debug level.</param>
/// <param name="FFmpeg">Where FFmpeg's shared libraries are, or null for the system's.</param>
/// <param name="Capture">The capture mode asked of a video input.</param>
/// <param name="Measure">Whether a subscriber measures a received test signal.</param>
/// <param name="FeedOutput">For <c>feed</c>, the output the input's video goes to.</param>
/// <param name="Serve">Serve WHIP and WHEP at <paramref name="Relay"/>, which is the URL to listen on.</param>
/// <param name="Insecure">Allows a plain ws relay on another host, for a test network.</param>
internal sealed record CommandLine(
    bool List,
    bool Publish,
    Uri Relay,
    string Room,
    string? Video,
    string? Audio,
    MediaSessionOptions Session,
    bool Verbose,
    string? FFmpeg,
    VideoInputRequest Capture,
    bool Measure,
    string? FeedOutput = null,
    bool Serve = false
)
{
    public bool Insecure { get; init; }

    public const string Usage = """
        streamtransport list
        streamtransport serve --urls <url> [--video <input>] [--audio <input>] [--measure]
                                                WHEP at /whep playing the inputs, WHIP at /whip/{name} into an
                                                output (or measured), a browser test page at /
        streamtransport analyze <file>          the A/V offset of the test signal in a recording, such as
                                                OBS's, at the recording's presentation times
        streamtransport feed <input> <output> [--pixel-format <f>]
                                                video from an input straight to an output, such as
                                                feed test v4l2:/dev/video42 for a webcam showing the test signal
        streamtransport publish|subscribe --relay <ws-url> --room <name> [options]

          --video <spec>     an input when publishing, an output when subscribing:
                             provider[:name]  such as spout, syphon, pipewire, v4l2, test
                             name             an input by its name, or camera for the first camera
                             none
          --audio <spec>     default          the default input when publishing, output when subscribing
                             output           what the default output plays (publish; Windows, Linux)
                             test             the test signal's clicks (publish)
                             none
          --size <WxH>       the capture size asked of a camera
          --fps <rate>       the capture rate asked of a camera
          --pixel-format <f> the pixel format asked of the input, such as nv12 or bgra
                             (the test signal's bgra carries alpha)
          --measure          subscribe to a test signal and report A/V offset and latency;
                             with --video the output still shows it (GPU frames unmeasured)
          --profile <name>   interactive | screen | irl | avatar
          --codec <name>     h264 | h265 | av1   the one video codec to offer
          --turn <url>       a turn: or turns: URL, used with --turn-user and --turn-password
          --ice-policy <p>   all | relay   relay sends only through TURN
          --ffmpeg <dir>     FFmpeg's shared libraries; defaults to the repository's native/ffmpeg/<rid>
          --insecure         allow a plain ws:// relay on another host (a test network only)
          --verbose          log at debug level

        Published video stays on the GPU from a Spout, Syphon or PipeWire source to the encoder,
        and received video from the decoder to the sink.
        """;

    public static CommandLine Parse(string[] args)
    {
        if (args is ["list"])
        {
            return new CommandLine(
                true,
                false,
                new Uri("ws://localhost"),
                string.Empty,
                null,
                null,
                MediaSessionOptions.For(MediaProfile.InteractiveP2P),
                false,
                RepositoryFFmpeg(),
                new VideoInputRequest(),
                false
            );
        }

        if (args is ["feed", string input, string output, .. string[] feedOptions])
        {
            Dictionary<string, string?> feedNamed = Named(feedOptions);
            return new CommandLine(
                false,
                true,
                new Uri("ws://localhost"),
                string.Empty,
                input,
                null,
                MediaSessionOptions.For(MediaProfile.InteractiveP2P),
                false,
                RepositoryFFmpeg(),
                new VideoInputRequest
                {
                    Size = new VideoSize(1280, 720),
                    FrameRate = 30,
                    PixelFormat = PixelFormatOption(feedNamed),
                },
                false,
                output
            );
        }

        if (args is ["serve", ..])
        {
            int urls = Array.IndexOf(args, "--urls");
            string listen =
                urls >= 0 && urls + 1 < args.Length ? args[urls + 1] : "http://[::]:8090";
            string[] rest =
            [
                .. args[1..].Where((_, i) => urls < 0 || (i != urls - 1 && i != urls)),
            ];
            return Parse(["publish", "--relay", listen, "--room", "serve", .. rest]) with
            {
                Serve = true,
            };
        }

        if (args.Length == 0 || args[0] is not ("publish" or "subscribe"))
        {
            throw new FormatException("Say list, serve, publish or subscribe first.");
        }

        Dictionary<string, string?> named = Named(args.AsSpan(1));

        MediaProfile profile = Choice(named, "profile") switch
        {
            null or "interactive" => MediaProfile.InteractiveP2P,
            "screen" => MediaProfile.ScreenShare,
            "irl" => MediaProfile.IrlContribution,
            "avatar" => MediaProfile.AvatarTransparent,
            string other => throw new FormatException($"No profile '{other}'."),
        };
        var session = MediaSessionOptions.For(profile);
        WebRtcTransportOptions transport = new()
        {
            ForwardErrorCorrection = session.Transport.ForwardErrorCorrection,
        };
        if (Choice(named, "codec") is { } codec)
        {
            session = session with
            {
                VideoCodecs =
                [
                    codec switch
                    {
                        "h264" => VideoCodecId.H264,
                        "h265" => VideoCodecId.H265,
                        "av1" => VideoCodecId.AV1,
                        _ => throw new FormatException($"No codec '{codec}'."),
                    },
                ],
            };
        }

        if (Value(named, "turn") is { } turn)
        {
            transport = transport with
            {
                IceServers =
                [
                    new IceServer(
                        [turn],
                        Value(named, "turn-user")
                            ?? throw new FormatException("--turn needs --turn-user."),
                        Value(named, "turn-password")
                            ?? throw new FormatException("--turn needs --turn-password.")
                    ),
                ],
            };
        }

        transport = transport with
        {
            IceTransportPolicy = Choice(named, "ice-policy") switch
            {
                null or "all" => IceTransportPolicy.All,
                "relay" => IceTransportPolicy.Relay,
                string other => throw new FormatException($"No ICE policy '{other}'."),
            },
        };

        session = session with { Transport = transport };

        VideoInputRequest capture = new()
        {
            Size = Value(named, "size") is { } size
                ? new VideoSize(
                    int.Parse(size.Split('x')[0], CultureInfo.InvariantCulture),
                    int.Parse(size.Split('x')[1], CultureInfo.InvariantCulture)
                )
                : null,
            FrameRate = Value(named, "fps") is { } fps
                ? double.Parse(fps, CultureInfo.InvariantCulture)
                : null,
            PixelFormat = PixelFormatOption(named),
        };
        return new CommandLine(
            false,
            args[0] == "publish",
            new Uri(Value(named, "relay") ?? throw new FormatException("--relay is required.")),
            Value(named, "room") ?? throw new FormatException("--room is required."),
            Value(named, "video") is "none" ? null : Value(named, "video") ?? Default(),
            Choice(named, "audio") is "none" ? null : Choice(named, "audio") ?? "default",
            session,
            named.ContainsKey("verbose"),
            Value(named, "ffmpeg") ?? RepositoryFFmpeg(),
            capture,
            named.ContainsKey("measure")
        )
        {
            Insecure = named.ContainsKey("insecure"),
        };
    }

    // The FFmpeg build eng/fetch-ffmpeg.ps1 puts beside a checkout, when the sample runs from one.
    internal static string? RepositoryFFmpeg()
    {
        for (
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent
        )
        {
            string natives = Path.Combine(
                directory.FullName,
                "native",
                "ffmpeg",
                RuntimeInformation.RuntimeIdentifier
            );
            if (Directory.Exists(natives))
            {
                return natives;
            }
        }

        return null;
    }

    // What this platform shares video on: the input or output by default.
    private static string Default() =>
        OperatingSystem.IsWindows() ? "spout"
        : OperatingSystem.IsMacOS() ? "syphon"
        : "pipewire";

    // Options as --name value pairs, with the few flags that take none.
    private static Dictionary<string, string?> Named(ReadOnlySpan<string> options)
    {
        Dictionary<string, string?> named = new(StringComparer.Ordinal);
        for (int i = 0; i < options.Length; i++)
        {
            string key = options[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                throw new FormatException($"Unexpected '{key}'.");
            }

            bool flag = key is "--verbose" or "--measure" or "--insecure";
            named[key[2..]] =
                flag ? null
                : i + 1 < options.Length ? options[++i]
                : throw new FormatException($"{key} needs a value.");
        }

        return named;
    }

    private static PixelFormat? PixelFormatOption(Dictionary<string, string?> named) =>
        Value(named, "pixel-format") is { } format
            ? Enum.TryParse(format, ignoreCase: true, out PixelFormat parsed)
            && Enum.IsDefined(parsed)
                ? parsed
                : throw new FormatException($"No pixel format '{format}'.")
            : null;

    private static string? Value(Dictionary<string, string?> named, string key) =>
        named.TryGetValue(key, out string? value) ? value : null;

    // A value that names one of a fixed set, in any case.
    private static string? Choice(Dictionary<string, string?> named, string key) =>
        Value(named, key)?.ToLower(CultureInfo.InvariantCulture);
}
