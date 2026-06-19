using Agash.StreamTransport;
using Agash.StreamTransport.Codecs;
using Spectre.Console;

namespace StreamTransport.Agent;

/// <summary>Publisher run loop: connect, capture, encode, fan out.</summary>
internal sealed class Publish(AgentMediaFactory media)
{
    public async Task<int> RunAsync(AgentConfig config, MediaSessionFactory transport, CancellationToken cancellationToken)
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
        IDisposable? audioCapture = null;
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
                var spout = media.CreateSpoutCapture(config.SpoutSender, encoderName, config.Alpha);
                gpuSource = spout;
                videoSource = spout;
                gpuDevice = spout.DeviceHandle;
                // Real desktop audio via WASAPI loopback ("what you hear") to accompany the captured surface;
                // --verify uses the synthetic tone so its A/V sync markers ride the stream.
                if (config.Audio)
                {
                    if (config.Verify)
                    {
                        audioSource = new SineToneAudioSource(config.Verify);
                    }
                    else
                    {
                        var cap = media.CreateWasapiCapture(loopback: true);
                        audioSource = cap;
                        audioCapture = cap;
                    }
                }
            }
#endif
#if HAS_PIPEWIRE
            else if (config.Source == VideoSourceKind.PipeWire && video && OperatingSystem.IsLinux())
            {
                var pw = await media.CreatePipeWireCaptureAsync(PipeWire.NET.PipeWireVideoCapture.AnyNode, config.Alpha);
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
                var syphon = media.CreateSyphonCapture(config.SyphonServer, config.Alpha);
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
                videoSource = video ? new TestPatternVideoSource(config.Width, config.Height, config.Fps, config.Alpha, config.Verify) : null;
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
                VideoFps = config.Fps,
                PreserveAlpha = config.Alpha,
                MaxVideoBFrames = config.BFrames > 0 ? config.BFrames : baseline.MaxVideoBFrames,
                LocalAddressPreferences = config.Interfaces ?? [],
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
            audioCapture?.Dispose();
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
internal sealed class Subscribe(AgentMediaFactory media)
{
    public async Task<int> RunAsync(AgentConfig config, MediaSessionFactory transport, CancellationToken cancellationToken)
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
        IAsyncDisposable? asyncPublishSink = null;
        IVideoFrameSink? videoSink;
        bool preferGpu = false;
        // A GPU publish sink does its own alpha unpack; it adopts the publisher's negotiated value here, so
        // the receiver never needs --alpha. The library's own receive pipeline auto-negotiates separately.
        Action<bool>? applyNegotiatedAlpha = null;
#if HAS_PIPEWIRE
        PipeWireAudioPublishSink? pipeWireAudio = null;
        PipeWireVideoPublishSink? pwVideoSink = null;
#endif
#if WINDOWS_HEAD
        WasapiAudioPublishSink? wasapiAudio = null;
#endif
#if MACOS_HEAD
        CoreAudioPublishSink? coreAudio = null;
#endif
#if HAS_SYPHON
        SyphonVideoPublishSink? syphonVideoSink = null;
#endif

#if HAS_PIPEWIRE
        if (config.PublishPipeWire is not null && OperatingSystem.IsLinux())
        {
            // Linux publish: a HW decoder (VAAPI) keeps frames on the GPU and the sink republishes them to
            // PipeWire as zero-copy as possible; a software-decoded CPU frame is the fallback the sink converts
            // to BGRA. Audio also goes to PipeWire (the Linux audio sink). preferGpu requests the GPU path.
            var pw = await media.CreatePipeWirePublishAsync(config.PublishPipeWire, config.Alpha);
            asyncPublishSink = pw;
            pwVideoSink = pw;
            videoSink = pw;
            applyNegotiatedAlpha = pw.SetPreserveAlpha;
            preferGpu = true;
            // Audio is published normally, except under --verify where the audio goes to the verifying sink
            // (so A/V sync is measured) rather than out to PipeWire.
            if (config.Audio && !config.Verify)
            {
                pipeWireAudio = await media.CreatePipeWireAudioPublishAsync($"{config.PublishPipeWire} Audio");
            }
        }
        else
#endif
#if WINDOWS_HEAD
        if (config.PublishSpout is not null)
        {
            var spout = media.CreateSpoutPublish(config.PublishSpout, config.Alpha);
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
            var syphon = media.CreateSyphonPublish(config.PublishSyphon, config.Alpha);
            publishSink = syphon;
            videoSink = syphon;
            syphonVideoSink = syphon;
            applyNegotiatedAlpha = syphon.SetPreserveAlpha;
            // Use the raw VTDecompressionSession decoder: it decodes straight to BGRA IOSurfaces (Syphon's
            // native format), so an opaque frame is announced zero-copy with no Metal convert on the receive
            // thread. For alpha the packed 2W x H BGRA surface is GPU-unpacked by the sink before publish.
            Agash.StreamTransport.Codecs.VideoDecoderBackendFactory.MacOsGpuDecoderFactory =
                static () => new VtSessionVideoDecoder();
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
#if HAS_PIPEWIRE
            if (pwVideoSink is not null && config.Video && OperatingSystem.IsLinux())
            {
                // GPU verify: keep publishing through the zero-copy GPU sink, but read each published surface
                // back to CPU and feed it to a verifying sink - so the real GPU output is checked for
                // content/alpha/sync identically to the CPU decode path. usePresentationTime so the readback
                // cost doesn't inflate the A/V skew. videoSink + preferGpu stay as set.
                var gpuVerify = new VerifyingVideoSink(report, usePresentationTime: true);
                pwVideoSink.EnableVerification(gpuVerify.Submit);
            }
            else
#endif
#if HAS_SYPHON
            if (syphonVideoSink is not null && config.Video && OperatingSystem.IsMacOS())
            {
                // GPU verify (macOS): keep publishing through the Syphon GPU sink (VideoToolbox decode -> Metal
                // convert/unpack), but read each published BGRA surface back to CPU for content/alpha checks.
                // Sync markers ride the NV12 luma so the BGRA readback leaves sync inconclusive (the CPU path
                // is the A/V-sync proof); video flow + content + alpha are verified on the real GPU output.
                var gpuVerify = new VerifyingVideoSink(report, usePresentationTime: true);
                syphonVideoSink.EnableVerification(gpuVerify.Submit);
            }
            else
#endif
            {
                videoSink = config.Video ? new VerifyingVideoSink(report) : null;
                preferGpu = false;
            }
        }

#if WINDOWS_HEAD
        // Windows receiver: play decoded audio to the default WASAPI render device (OBS "Desktop Audio" / app
        // capture + the system hear it), unless --verify routes audio to the verifying sink to measure A/V sync.
        if (config.Audio && !config.Verify && OperatingSystem.IsWindows())
        {
            wasapiAudio = media.CreateWasapiPublish();
        }
#endif
#if MACOS_HEAD
        // macOS receiver: play decoded audio to the default output device (CoreAudio), unless --verify routes
        // audio to the verifying sink to measure A/V sync.
        if (config.Audio && !config.Verify && OperatingSystem.IsMacOS())
        {
            coreAudio = media.CreateCoreAudioPublish();
        }
#endif

        MediaTransportOptions baseline = MediaProfiles.Create(config.Profile);
        var options = baseline with
        {
            PreferGpuVideoOutput = preferGpu,
            PreserveAlpha = config.Alpha,
            PlayoutMode = config.Synced ? PlayoutMode.Synced : baseline.PlayoutMode,
            LocalAddressPreferences = config.Interfaces ?? [],
        };
        AnsiConsole.MarkupLineInterpolated($"connecting to [teal]{config.Relay}[/] as subscriber of room [teal]{config.Room}[/]...");
        await using RoomClient room = await RoomClient.ConnectAsync(
            config.Relay!, new RoomCode(config.Room), PeerRole.Subscriber, cancellationToken);

        IAudioFrameSink? audioSink = !config.Audio ? null
            : config.Verify ? new VerifyingAudioSink(report!)
#if HAS_PIPEWIRE
            : pipeWireAudio is not null ? pipeWireAudio
#endif
#if WINDOWS_HEAD
            : wasapiAudio is not null ? wasapiAudio
#endif
#if MACOS_HEAD
            : coreAudio is not null ? coreAudio
#endif
            : new ReportingAudioSink(m => AnsiConsole.MarkupLineInterpolated($"[blue]{m}[/]"));

        using (publishSink)
#if WINDOWS_HEAD
        using (wasapiAudio)
#endif
#if MACOS_HEAD
        using (coreAudio)
#endif
        await using (asyncPublishSink)
#if HAS_PIPEWIRE
        await using (pipeWireAudio)
#endif
        await using (MediaSubscriber subscriber = transport.CreateSubscriber(options, room, videoSink, audioSink))
        {
            if (applyNegotiatedAlpha is not null)
            {
                subscriber.AlphaNegotiated += applyNegotiatedAlpha;
                // Apply a value that already arrived: the publisher sends stream.alpha on the ordered control
                // channel before its offer, so under auto-negotiation it can land before this subscription. The
                // event is fire-and-forget, so without this the GPU publish sink would miss it and commit its
                // output pool as NV12 at first frame (alpha lost). (#11)
                if (subscriber.NegotiatedAlpha is { } alreadyNegotiated)
                {
                    applyNegotiatedAlpha(alreadyNegotiated);
                }
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

                // The measurement window is done and the verdict is printed. Flush it and hard-exit instead of
                // unwinding through sink disposal: native teardown (Syphon / CoreAudio / PipeWire) can block on
                // some platforms, which would hold the process - and an orchestrator's SSH pipe - open long past
                // the window and strand the just-printed verdict in an unflushed buffer (the cause of macOS
                // loopback NO-REPORTs in the verify matrix). The OS reclaims the native handles on exit.
                Console.Out.Flush();
                Console.Error.Flush();
                Environment.Exit(0);
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
