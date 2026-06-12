using Agash.StreamTransport;
using Agash.StreamTransport.Codecs;
using Spectre.Console;

namespace StreamTransport.Agent;

/// <summary>Publisher run loop: connect, capture, encode, fan out.</summary>
internal static class Publish
{
    public static async Task<int> RunAsync(AgentConfig config, MediaSessionFactory transport, CancellationToken cancellationToken)
    {
        bool video = config.Video;
        if (video && !FfmpegReady())
        {
            AnsiConsole.MarkupLine("[yellow]FFmpeg natives not found - falling back to audio-only.[/]");
            video = false;
        }

        FFmpegCaptureDevice? videoDevice = null;
        FFmpegCaptureDevice? audioDevice = null;
        IDisposable? gpuSource = null;
        IAsyncDisposable? asyncSource = null;
        IVideoFrameSource? videoSource = null;
        IAudioFrameSource? audioSource = null;
        nint gpuDevice = 0;
        string? encoderName = config.EncoderName;
        EmbeddedRelay? embedded = null;
        SignalingTunnel? tunnel = null;

        try
        {
            if (config.Source == VideoSourceKind.Camera && video)
            {
                videoDevice = CameraCapture.OpenVideo(config.VideoDevice!);
                videoSource = videoDevice.Video;
                if (!string.IsNullOrWhiteSpace(config.AudioDevice))
                {
                    audioDevice = CameraCapture.OpenAudio(config.AudioDevice!);
                    audioSource = audioDevice.Audio;
                }
            }
#if WINDOWS_HEAD
            else if (config.Source == VideoSourceKind.Spout && video)
            {
                // Spout surfaces are BGRA, encoded directly by nvenc with no conversion (zero-copy).
                encoderName ??= "hevc_nvenc";
                var spout = new SpoutVideoCaptureSource(config.SpoutSender, encoderName, config.Alpha);
                gpuSource = spout;
                videoSource = spout;
                gpuDevice = spout.DeviceHandle;
                audioSource = config.Audio ? new SineToneAudioSource() : null;
            }
#endif
#if HAS_PIPEWIRE
            else if (config.Source == VideoSourceKind.PipeWire && video && OperatingSystem.IsLinux())
            {
                var pw = await PipeWireVideoCaptureSource.CreateAsync(PipeWire.NET.PipeWireVideoCapture.AnyNode, config.Alpha);
                asyncSource = pw;
                videoSource = pw;
                encoderName ??= "hevc_vaapi"; // Linux Intel/AMD; override with --encoder for nvenc.
                audioSource = config.Audio ? new SineToneAudioSource() : null;
            }
#endif
#if HAS_SYPHON
            else if (config.Source == VideoSourceKind.Syphon && video && OperatingSystem.IsMacOS())
            {
                // Discover a Syphon server through the directory; --syphon-server targets one by name,
                // otherwise the first advertised server is used.
                var syphon = SyphonVideoCaptureSource.Connect(config.SyphonServer, config.Alpha);
                gpuSource = syphon;
                videoSource = syphon;
                encoderName ??= "hevc_videotoolbox"; // Apple silicon/Intel Mac; VideoToolbox takes BGRA directly.
                audioSource = config.Audio ? new SineToneAudioSource() : null;
            }
#endif
            else
            {
                if (config.Source == VideoSourceKind.Spout)
                {
                    AnsiConsole.MarkupLine("[yellow]Spout capture is only available in the Windows build; using the test pattern.[/]");
                }

                // --verify makes the synthetic source emit correlated A/V sync markers for the receiver.
                videoSource = video ? new TestPatternVideoSource(1280, 720, 30, config.Alpha, config.Verify) : null;
                audioSource = config.Audio ? new SineToneAudioSource(config.Verify) : null;
            }

            if (videoSource is null && audioSource is null)
            {
                AnsiConsole.MarkupLine("[red]nothing to publish (no usable video or audio source).[/]");
                return 1;
            }

            // Resolve where the publisher connects, and the URL receivers should use.
            Uri connectUri;
            string receiverRelay;
            if (config.Host)
            {
                embedded = EmbeddedRelay.Start();
                connectUri = embedded.LocalWebSocketUri;
                if (config.DevTunnel)
                {
                    AnsiConsole.MarkupLine("[grey]starting DevTunnel (this uses the authenticated devtunnel CLI)...[/]");
                    tunnel = await SignalingTunnel.StartAsync(embedded.Port, cancellationToken);
                    receiverRelay = tunnel.PublicWebSocketUri.ToString();
                }
                else
                {
                    receiverRelay = $"ws://<this-host>:{embedded.Port}/ws";
                }
            }
            else
            {
                connectUri = config.Relay!;
                receiverRelay = config.Relay!.ToString();
            }

            // Start from the profile preset; explicit flags override (B-frames only when a positive value is passed).
            MediaTransportOptions baseline = MediaProfiles.Create(config.Profile);
            var options = baseline with
            {
                VideoEncoderName = encoderName,
                PreserveAlpha = config.Alpha,
                MaxVideoBFrames = config.BFrames > 0 ? config.BFrames : baseline.MaxVideoBFrames,
            };
            AnsiConsole.MarkupLineInterpolated($"connecting to [teal]{connectUri}[/] as publisher...");
            await using RoomClient room = await RoomClient.ConnectAsync(
                connectUri, new RoomCode(config.Room), PeerRole.Publisher, cancellationToken);

            await using MediaPublisher publisher = transport.CreatePublisher(options, room, videoSource, audioSource, gpuDevice);
            publisher.Start();

            AnsiConsole.Write(SessionPanel(config, room, videoSource, audioSource));
            AnsiConsole.MarkupLineInterpolated(
                $"receivers run: [grey]receive --relay {receiverRelay} --room {config.Room}[/]");
            AnsiConsole.MarkupLine("[grey]publishing; press Ctrl+C to stop.[/]");
            if (config.Verify)
            {
                // Bounded run so the publisher disposes gracefully (and any sync diagnostics flush). Outlast
                // the receiver's own --seconds window so it always has a peer for its whole measurement.
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bounded.CancelAfter(TimeSpan.FromSeconds(config.Seconds + 8));
                await WaitAsync(bounded.Token);
            }
            else
            {
                await WaitAsync(cancellationToken);
            }

            return 0;
        }
        finally
        {
            gpuSource?.Dispose();
            if (asyncSource is not null)
            {
                await asyncSource.DisposeAsync();
            }

            videoDevice?.Dispose();
            audioDevice?.Dispose();
            if (tunnel is not null)
            {
                await tunnel.DisposeAsync();
            }

            if (embedded is not null)
            {
                await embedded.DisposeAsync();
            }
        }
    }

    private static Panel SessionPanel(AgentConfig config, RoomClient room, IVideoFrameSource? v, IAudioFrameSource? a)
    {
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]room[/]", config.Room);
        grid.AddRow("[grey]peer id[/]", room.Self.ToString());
        grid.AddRow("[grey]video[/]", v is null ? "off" : config.Source.ToString().ToLowerInvariant());
        grid.AddRow("[grey]audio[/]", a is null ? "off" : "on (synced)");
        grid.AddRow("[grey]ice servers[/]", room.IceServers.Count.ToString());
        return new Panel(grid).Header("publishing").BorderColor(Color.Teal);
    }

    private static bool FfmpegReady()
    {
        try
        {
            FFmpegLibrary.EnsureLoaded();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static async Task WaitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C.
        }
    }
}

/// <summary>Subscriber run loop: connect, attach to the publisher, decode into reporting sinks.</summary>
internal static class Subscribe
{
    public static async Task<int> RunAsync(AgentConfig config, MediaSessionFactory transport, CancellationToken cancellationToken)
    {
        try
        {
            FFmpegLibrary.EnsureLoaded();
        }
        catch (Exception)
        {
            AnsiConsole.MarkupLine("[yellow]FFmpeg natives not found - video decode unavailable; audio only.[/]");
        }

        IDisposable? publishSink = null;
        IVideoFrameSink? videoSink;
        bool preferGpu = false;
        // A GPU publish sink does its own alpha unpack; it adopts the publisher's negotiated value here, so
        // the receiver never needs --alpha. The library's own receive pipeline auto-negotiates separately.
        Action<bool>? applyNegotiatedAlpha = null;

#if WINDOWS_HEAD
        if (config.PublishSpout is not null)
        {
            var spout = new SpoutVideoPublishSink(config.PublishSpout, config.Alpha);
            publishSink = spout;
            videoSink = spout;
            applyNegotiatedAlpha = spout.SetPreserveAlpha;
            preferGpu = true; // decode straight into a GPU texture for zero-copy republish.
        }
        else
#endif
#if HAS_SYPHON
        if (config.PublishSyphon is not null && OperatingSystem.IsMacOS())
        {
            var syphon = new SyphonVideoPublishSink(config.PublishSyphon, config.Alpha);
            publishSink = syphon;
            videoSink = syphon;
            applyNegotiatedAlpha = syphon.SetPreserveAlpha;
            // VideoToolbox decodes straight into a GPU surface for a zero-copy Syphon publish. For alpha
            // the surface is the packed 2W x H frame, which the sink GPU-unpacks via Metal before publish.
            preferGpu = true;
        }
        else
#endif
        {
            videoSink = config.Video ? new ReportingVideoSink(m => AnsiConsole.MarkupLineInterpolated($"[green]{m}[/]")) : null;
        }

        // --verify swaps in the verifying sinks: a CPU decode (so the sink sees pixels - alpha unpacks to
        // BGRA, opaque to NV12/I420) feeding content + A/V-sync checks. Run without a --publish flag.
        VerificationReport? report = null;
        if (config.Verify)
        {
            report = new VerificationReport();
            videoSink = config.Video ? new VerifyingVideoSink(report) : null;
            preferGpu = false;
        }

        MediaTransportOptions baseline = MediaProfiles.Create(config.Profile);
        var options = baseline with
        {
            PreferGpuVideoOutput = preferGpu,
            PreserveAlpha = config.Alpha,
            PlayoutMode = config.Synced ? PlayoutMode.Synced : baseline.PlayoutMode,
        };
        AnsiConsole.MarkupLineInterpolated($"connecting to [teal]{config.Relay}[/] as subscriber of room [teal]{config.Room}[/]...");
        await using RoomClient room = await RoomClient.ConnectAsync(
            config.Relay!, new RoomCode(config.Room), PeerRole.Subscriber, cancellationToken);

        IAudioFrameSink? audioSink = !config.Audio ? null
            : config.Verify ? new VerifyingAudioSink(report!)
            : new ReportingAudioSink(m => AnsiConsole.MarkupLineInterpolated($"[blue]{m}[/]"));

        using (publishSink)
        await using (MediaSubscriber subscriber = transport.CreateSubscriber(options, room, videoSink, audioSink))
        {
            if (applyNegotiatedAlpha is not null)
            {
                subscriber.AlphaNegotiated += applyNegotiatedAlpha;
            }

            await subscriber.StartAsync(cancellationToken);

            if (report is not null)
            {
                // --verify runs a fixed window then auto-prints the report and exits - scriptable, no Ctrl+C
                // needed (Ctrl+C still ends it early). Robust for remotely orchestrated runs.
                AnsiConsole.MarkupLineInterpolated(
                    $"joined as peer [teal]{room.Self}[/]; --verify collecting for {config.Seconds}s (content + A/V sync)...");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(config.Seconds), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Ctrl+C ends the window early.
                }

                report.Print(m => AnsiConsole.MarkupLineInterpolated($"[teal]{m}[/]"), config.Video, config.Audio);
            }
            else
            {
                AnsiConsole.MarkupLineInterpolated($"joined as peer [teal]{room.Self}[/]; {room.Peers.Count} peer(s) present. receiving; Ctrl+C to stop.");
                await Publish.WaitAsync(cancellationToken);
            }

            return 0;
        }
    }
}

/// <summary>Builds platform device URLs and opens libavdevice capture for a camera and a microphone.</summary>
internal static class CameraCapture
{
    public static FFmpegCaptureDevice OpenVideo(string device) =>
        FFmpegCaptureDevice.Open(CaptureBackend.VideoInputFormat, VideoUrl(device), VideoOptions());

    public static FFmpegCaptureDevice OpenAudio(string device) =>
        FFmpegCaptureDevice.Open(CaptureBackend.AudioInputFormat, AudioUrl(device));

    private static string VideoUrl(string device) =>
        OperatingSystem.IsWindows() ? $"video={device}"
        : OperatingSystem.IsMacOS() ? device         // AVFoundation: index or name.
        : device;                                     // v4l2: /dev/videoN.

    private static string AudioUrl(string device) =>
        OperatingSystem.IsWindows() ? $"audio={device}"
        : OperatingSystem.IsMacOS() ? $":{device}"    // AVFoundation: ":audio".
        : device;                                     // ALSA: device name.

    private static Dictionary<string, string> VideoOptions() =>
        new() { ["framerate"] = "30", ["video_size"] = "1280x720" };
}
