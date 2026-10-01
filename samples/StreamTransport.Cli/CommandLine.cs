using System.Globalization;
using System.Runtime.InteropServices;
using Agash.StreamTransport;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.WebRtc.Ice;

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
    bool Measure
)
{
    public const string Usage = """
        streamtransport list
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
          --measure          subscribe to a test signal and report A/V offset and latency
          --profile <name>   interactive | screen | irl | avatar
          --codec <name>     h264 | h265 | av1   the one video codec to offer
          --turn <url>       a turn: or turns: URL, used with --turn-user and --turn-password
          --ice-policy <p>   all | relay   relay sends only through TURN
          --ffmpeg <dir>     FFmpeg's shared libraries; defaults to the repository's native/ffmpeg/<rid>
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

        if (args.Length == 0 || args[0] is not ("publish" or "subscribe"))
        {
            throw new FormatException("Say list, publish or subscribe first.");
        }

        Dictionary<string, string?> named = new(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i++)
        {
            string key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                throw new FormatException($"Unexpected '{key}'.");
            }

            bool flag = key is "--verbose" or "--measure";
            named[key[2..]] =
                flag ? null
                : i + 1 < args.Length ? args[++i]
                : throw new FormatException($"{key} needs a value.");
        }

        MediaProfile profile = Choice(named, "profile") switch
        {
            null or "interactive" => MediaProfile.InteractiveP2P,
            "screen" => MediaProfile.ScreenShare,
            "irl" => MediaProfile.IrlContribution,
            "avatar" => MediaProfile.AvatarTransparent,
            string other => throw new FormatException($"No profile '{other}'."),
        };
        var session = MediaSessionOptions.For(profile);
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
            session = session with
            {
                IceServers =
                [
                    new IceServer(
                        [turn],
                        Value(named, "turn-user") ?? throw new FormatException("--turn needs --turn-user."),
                        Value(named, "turn-password")
                            ?? throw new FormatException("--turn needs --turn-password.")
                    ),
                ],
            };
        }

        session = session with
        {
            IceTransportPolicy = Choice(named, "ice-policy") switch
            {
                null or "all" => IceTransportPolicy.All,
                "relay" => IceTransportPolicy.Relay,
                string other => throw new FormatException($"No ICE policy '{other}'."),
            },
        };

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
        );
    }

    // The FFmpeg build eng/fetch-ffmpeg.ps1 puts beside a checkout, when the sample runs from one.
    private static string? RepositoryFFmpeg()
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

    private static string? Value(Dictionary<string, string?> named, string key) =>
        named.TryGetValue(key, out string? value) ? value : null;

    // A value that names one of a fixed set, in any case.
    private static string? Choice(Dictionary<string, string?> named, string key) =>
        Value(named, key)?.ToLower(CultureInfo.InvariantCulture);
}
