using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Agash.StreamTransport.AspNetCore;
using Agash.StreamTransport.Codecs.FFmpeg;
using Agash.StreamTransport.Codecs.Opus;
using Agash.StreamTransport.Http;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.TestSignal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Agash.StreamTransport.Tests;

// WHIP and WHEP through a real Kestrel host on loopback: the endpoints mapped into an ordinary ASP.NET
// Core application, the clients talking to it over HTTP, the media over ICE, DTLS and SRTP.
[TestClass]
[TestCategory("Integration")]
public sealed class HttpMediaTests
{
    private const string AvOffset = "streamtransport.playout.av_offset";

    public TestContext TestContext { get; set; } = null!;

    // A video sink that takes 100 ms to show a frame: the session plays audio 100 ms later to match, from
    // the sink's own OutputLatency, with nothing set by the application.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Whip_VideoSinkLatency_DelaysAudioToMatch()
    {
        AnalyzerWatch watch = new(new TestSignalAnalyzer());
        TestSignalAnalyzer analyzer = watch.Analyzer;
        await using WebApplication app = await StartAsync(web =>
            web.MapWhip(
                "/whip",
                (_, _) =>
                    ValueTask.FromResult<HttpMediaSetup?>(
                        new HttpMediaSetup(
                            new MediaEndpoints
                            {
                                VideoSink = watch.WrapVideo(new LatentVideoSink()),
                                AudioSink = watch.WrapAudio(),
                            },
                            Loopback()
                        )
                    )
            )
        );
        TestSignalGenerator generator = new();
        using IVideoInput video = await new TestSignalVideoInputProvider(generator).OpenAsync(
            new VideoInputInfo("test", "signal", "Test signal", MediaInputKind.Generated, []),
            new VideoInputRequest { Size = new VideoSize(640, 360), FrameRate = 30 },
            CancellationToken.None
        );
        using IAudioInput audio = await new TestSignalAudioInputProvider(generator).OpenAsync(
            new AudioInputInfo("test", "signal", "Test signal", MediaInputKind.Generated),
            CancellationToken.None
        );
        HttpMediaSession publishing = await WhipClient.PublishAsync(
            app.Services.GetRequiredService<IMediaSessionFactory>(),
            new Uri(Address(app), "/whip"),
            new MediaEndpoints { VideoSource = video, AudioSource = audio },
            Loopback()
        );
        await using (publishing)
        {
            await publishing.Session.Connected.WaitAsync(TimeSpan.FromSeconds(20));
            await watch.UntilAsync(
                m => m.Pairs >= 1,
                TimeSpan.FromSeconds(30),
                TestContext.CancellationToken
            );
            analyzer.Reset();
            await watch.UntilAsync(
                m => m.Pairs >= 3,
                TimeSpan.FromSeconds(30),
                TestContext.CancellationToken
            );
        }

        TestSignalMeasurement measured = analyzer.Measure();
        TestContext.WriteLine(measured.ToString());
        Assert.IsLessThan(
            40,
            Math.Abs(
                measured.MeanOffset.TotalMilliseconds - LatentVideoSink.Latency.TotalMilliseconds
            ),
            measured.ToString()
        );
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Whip_PublishedTestSignal_ArrivesAndIsInSync()
    {
        AnalyzerWatch watch = new(new TestSignalAnalyzer());
        TestSignalAnalyzer analyzer = watch.Analyzer;
        using MeterRecorder meters = new();
        int measuredFrom = 0;
        int ended = 0;
        TaskCompletionSource endedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using WebApplication app = await StartAsync(
            web =>
                web.MapWhip(
                    "/whip/{room}",
                    (context, _) =>
                        ValueTask.FromResult<HttpMediaSetup?>(
                            (string?)context.Request.RouteValues["room"] == "studio"
                                ? new HttpMediaSetup(
                                    new MediaEndpoints
                                    {
                                        VideoSink = watch.WrapVideo(),
                                        AudioSink = watch.WrapAudio(),
                                    },
                                    Loopback()
                                )
                                {
                                    Ended = () =>
                                    {
                                        Interlocked.Increment(ref ended);
                                        endedSignal.TrySetResult();
                                        return ValueTask.CompletedTask;
                                    },
                                }
                                : null
                        )
                ),
            services => services.AddSingleton<IMeterFactory>(meters)
        );
        IServiceProvider services = app.Services;
        TestSignalGenerator generator = new();
        using IVideoInput video = await new TestSignalVideoInputProvider(generator).OpenAsync(
            new VideoInputInfo("test", "signal", "Test signal", MediaInputKind.Generated, []),
            new VideoInputRequest { Size = new VideoSize(640, 360), FrameRate = 30 },
            CancellationToken.None
        );
        using IAudioInput audio = await new TestSignalAudioInputProvider(generator).OpenAsync(
            new AudioInputInfo("test", "signal", "Test signal", MediaInputKind.Generated),
            CancellationToken.None
        );

        HttpMediaSession publishing = await WhipClient.PublishAsync(
            services.GetRequiredService<IMediaSessionFactory>(),
            new Uri(Address(app), "/whip/studio"),
            new MediaEndpoints { VideoSource = video, AudioSource = audio },
            Loopback()
        );
        await using (publishing)
        {
            Assert.IsNotNull(publishing.Resource);
            await publishing.Session.Connected.WaitAsync(TimeSpan.FromSeconds(20));

            // The first second is the session settling; the steady state is what is measured.
            await watch.UntilAsync(
                m => m.Pairs >= 1,
                TimeSpan.FromSeconds(30),
                TestContext.CancellationToken
            );
            analyzer.Reset();
            measuredFrom = meters.Count(AvOffset);
            await watch.UntilAsync(
                m => m.Pairs >= 3,
                TimeSpan.FromSeconds(30),
                TestContext.CancellationToken
            );
        }

        await endedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);

        TestSignalMeasurement measured = analyzer.Measure();
        Assert.IsLessThan(60, Math.Abs(measured.MeanOffset.TotalMilliseconds), measured.ToString());

        // The session's own lip-sync measurement agrees with the test signal's, within a video frame and an
        // audio frame: it is what tells a real stream's sync, where there is no test signal.
        double monitored = meters.Values(AvOffset).Skip(measuredFrom).Average() * 1000;
        TestContext.WriteLine($"session A/V offset {monitored:0.0} ms; test signal {measured}");

        // Where timing frames' latency went, stage by stage, as the session reports it: medians, since the
        // first frames carry the encoder opening and the decoder waiting for a keyframe.
        string[] stages =
        [
            "encode_queue",
            "encode",
            "packetize",
            "pacer",
            "network",
            "receive",
            "assembly",
            "decode",
            "playout",
        ];
        double[] endToEnd = meters.Values("streamtransport.video.timing.end_to_end");
        Assert.IsNotEmpty(endToEnd, "timing frames were reported");
        TestContext.WriteLine(
            "timing frames: "
                + string.Join(
                    ", ",
                    stages.Select(stage =>
                        $"{stage} {Percentile(meters.Values("streamtransport.video.timing.stage", stage), 0.5):0.0}"
                    )
                )
                + $"; end to end {Percentile(endToEnd, 0.5):0.0} ms median over {endToEnd.Length}"
        );
        double[] encodeCalls = meters.Values("streamtransport.video.encode.duration");
        TestContext.WriteLine(
            $"encoder calls {Mean(encodeCalls):0.0} ms mean, {Percentile(encodeCalls, 0.95):0.0} ms p95 over {encodeCalls.Length}; frames dropped busy {meters.Count("streamtransport.video.frames.dropped")}"
        );
        Assert.IsLessThan(
            35,
            Math.Abs(monitored - measured.MeanOffset.TotalMilliseconds),
            $"session {monitored:0.0} ms, test signal {measured}"
        );
        Assert.AreEqual(1, Volatile.Read(ref ended), "the DELETE ended the server's session");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Whep_PlayedTestSignal_Arrives()
    {
        TestSignalGenerator generator = new();
        using IVideoInput video = await new TestSignalVideoInputProvider(generator).OpenAsync(
            new VideoInputInfo("test", "signal", "Test signal", MediaInputKind.Generated, []),
            new VideoInputRequest { Size = new VideoSize(640, 360), FrameRate = 30 },
            CancellationToken.None
        );
        using IAudioInput audio = await new TestSignalAudioInputProvider(generator).OpenAsync(
            new AudioInputInfo("test", "signal", "Test signal", MediaInputKind.Generated),
            CancellationToken.None
        );
        await using WebApplication app = await StartAsync(web =>
            web.MapWhep(
                "/whep",
                (_, _) =>
                    ValueTask.FromResult<HttpMediaSetup?>(
                        new HttpMediaSetup(
                            new MediaEndpoints { VideoSource = video, AudioSource = audio },
                            Loopback()
                        )
                    )
            )
        );
        AnalyzerWatch watch = new(new TestSignalAnalyzer());

        HttpMediaSession playing = await WhepClient.PlayAsync(
            app.Services.GetRequiredService<IMediaSessionFactory>(),
            new Uri(Address(app), "/whep"),
            new MediaEndpoints { VideoSink = watch.WrapVideo(), AudioSink = watch.WrapAudio() },
            Loopback()
        );
        await using (playing)
        {
            await playing.Session.Connected.WaitAsync(TimeSpan.FromSeconds(20));

            // Two A/V pairs can be measured a frame short of a second of video, so both are waited for.
            await watch.UntilAsync(
                m => m is { Pairs: >= 2, Frames: > 30 },
                TimeSpan.FromSeconds(20),
                TestContext.CancellationToken
            );
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Endpoints_AnswerTheProtocolsStatusCodes()
    {
        await using WebApplication app = await StartAsync(web =>
            web.MapWhip("/whip/{room}", (_, _) => ValueTask.FromResult<HttpMediaSetup?>(null))
        );
        using HttpClient http = new() { BaseAddress = Address(app) };

        using HttpResponseMessage wrongType = await http.PostAsync(
            "/whip/x",
            new StringContent("v=0", Encoding.UTF8, "text/plain")
        );
        using HttpResponseMessage notSdp = await http.PostAsync(
            "/whip/x",
            new StringContent("hello", Encoding.UTF8, "application/sdp")
        );
        using HttpResponseMessage options = await http.SendAsync(
            new HttpRequestMessage(HttpMethod.Options, "/whip/x")
        );
        using HttpResponseMessage patch = await http.PatchAsync(
            "/whip/x/abc",
            new StringContent("a=candidate", Encoding.UTF8, "application/trickle-ice-sdpfrag")
        );
        using HttpResponseMessage delete = await http.DeleteAsync("/whip/x/abc");
        using HttpResponseMessage resourceOptions = await http.SendAsync(
            new HttpRequestMessage(HttpMethod.Options, "/whip/x/abc")
        );
        using HttpResponseMessage twoVideos = await http.PostAsync(
            "/whip/x",
            new StringContent(TwoVideoOffer, Encoding.UTF8, "application/sdp")
        );
        using HttpResponseMessage huge = await http.PostAsync(
            "/whip/x",
            new StringContent(new string('a', 70_000), Encoding.UTF8, "application/sdp")
        );

        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, notSdp.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, options.StatusCode, "RFC 9725 section 4.2");
        Assert.AreEqual("application/sdp", options.Headers.GetValues("Accept-Post").Single());
        Assert.AreEqual(HttpStatusCode.NotFound, patch.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, resourceOptions.StatusCode, "a browser's preflight");
        Assert.AreEqual(
            "application/trickle-ice-sdpfrag",
            resourceOptions.Headers.GetValues("Accept-Patch").Single()
        );
        Assert.AreEqual(
            HttpStatusCode.UnprocessableEntity,
            twoVideos.StatusCode,
            "RFC 9725 section 4.4.2"
        );
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
    }

    private const string TwoVideoOffer =
        "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n"
        + "a=fingerprint:sha-256 00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF\r\n"
        + "a=ice-ufrag:abcd\r\na=ice-pwd:0123456789abcdef0123456789\r\na=setup:actpass\r\n"
        + "m=video 9 UDP/TLS/RTP/SAVPF 96\r\nc=IN IP4 0.0.0.0\r\na=mid:0\r\na=sendonly\r\na=rtcp-mux\r\n"
        + "a=rtpmap:96 H264/90000\r\n"
        + "m=video 9 UDP/TLS/RTP/SAVPF 96\r\nc=IN IP4 0.0.0.0\r\na=mid:1\r\na=sendonly\r\na=rtcp-mux\r\n"
        + "a=rtpmap:96 H264/90000\r\n";

    [TestMethod]
    [Timeout(60_000)]
    public async Task Patch_OnALiveResource_TakesTrickledCandidatesForItsIceSession()
    {
        AnalyzerWatch watch = new(new TestSignalAnalyzer());
        await using WebApplication app = await StartAsync(web =>
            web.MapWhip(
                "/whip",
                (_, _) =>
                    ValueTask.FromResult<HttpMediaSetup?>(
                        new HttpMediaSetup(
                            new MediaEndpoints { AudioSink = watch.WrapAudio() },
                            Loopback()
                        )
                    )
            )
        );
        using IAudioInput audio = await new TestSignalAudioInputProvider(
            new TestSignalGenerator()
        ).OpenAsync(
            new AudioInputInfo("test", "signal", "Test signal", MediaInputKind.Generated),
            CancellationToken.None
        );
        HttpMediaSession publishing = await WhipClient.PublishAsync(
            app.Services.GetRequiredService<IMediaSessionFactory>(),
            new Uri(Address(app), "/whip"),
            new MediaEndpoints { AudioSource = audio },
            Loopback()
        );
        await using (publishing)
        {
            await publishing.Session.Connected.WaitAsync(TimeSpan.FromSeconds(20));
            Uri resource = publishing.Resource!;
            string tag = $"\"{resource.Segments[^1]}\"";
            using HttpClient http = new();
            const string Candidate = "a=candidate:9 1 udp 2130706431 127.0.0.1 9 typ host";

            async Task<HttpStatusCode> PatchAsync(
                string body,
                string? ifMatch,
                string type = TrickleIceFragment.MediaType
            )
            {
                using HttpRequestMessage request = new(HttpMethod.Patch, resource)
                {
                    Content = new StringContent(body, Encoding.UTF8, type),
                };
                if (ifMatch is not null)
                {
                    _ = request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
                }

                using HttpResponseMessage response = await http.SendAsync(request);
                return response.StatusCode;
            }

            string trickle = $"m=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:0\r\n{Candidate}\r\n";
            Assert.AreEqual(HttpStatusCode.PreconditionRequired, await PatchAsync(trickle, null));
            Assert.AreEqual(
                HttpStatusCode.PreconditionFailed,
                await PatchAsync(trickle, "\"another\"")
            );
            Assert.AreEqual(
                HttpStatusCode.UnsupportedMediaType,
                await PatchAsync(trickle, tag, "text/plain")
            );
            Assert.AreEqual(HttpStatusCode.BadRequest, await PatchAsync("hello", tag));
            Assert.AreEqual(HttpStatusCode.NoContent, await PatchAsync(trickle, tag));
            Assert.AreEqual(TransportState.Connected, publishing.Session.State);

            // New credentials restart ICE: 200, the server's new credentials and a new tag; the old tag
            // names an ICE session that is gone.
            using HttpRequestMessage restart = new(HttpMethod.Patch, resource)
            {
                Content = new StringContent(
                    "a=ice-ufrag:fresh\r\na=ice-pwd:freshfreshfreshfreshfr\r\n" + trickle,
                    Encoding.UTF8,
                    TrickleIceFragment.MediaType
                ),
            };
            _ = restart.Headers.TryAddWithoutValidation("If-Match", "*");
            using HttpResponseMessage restarted = await http.SendAsync(restart);
            Assert.AreEqual(HttpStatusCode.OK, restarted.StatusCode);
            Assert.IsTrue(
                TrickleIceFragment.TryParse(
                    await restarted.Content.ReadAsStringAsync(),
                    out TrickleIceFragment server
                )
            );
            Assert.IsNotNull(server.UsernameFragment);
            Assert.IsNotEmpty(server.Candidates);
            string newTag = restarted.Headers.ETag!.Tag;
            Assert.AreNotEqual(tag, newTag);
            Assert.AreEqual(HttpStatusCode.PreconditionFailed, await PatchAsync(trickle, tag));
            Assert.AreEqual(HttpStatusCode.NoContent, await PatchAsync(trickle, newTag));
        }
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task Whip_IceRestart_KeepsMediaFlowing()
    {
        AnalyzerWatch watch = new(new TestSignalAnalyzer());
        TestSignalAnalyzer analyzer = watch.Analyzer;
        await using WebApplication app = await StartAsync(web =>
            web.MapWhip(
                "/whip",
                (_, _) =>
                    ValueTask.FromResult<HttpMediaSetup?>(
                        new HttpMediaSetup(
                            new MediaEndpoints
                            {
                                VideoSink = watch.WrapVideo(),
                                AudioSink = watch.WrapAudio(),
                            },
                            Loopback()
                        )
                    )
            )
        );
        TestSignalGenerator generator = new();
        using IVideoInput video = await new TestSignalVideoInputProvider(generator).OpenAsync(
            new VideoInputInfo("test", "signal", "Test signal", MediaInputKind.Generated, []),
            new VideoInputRequest { Size = new VideoSize(640, 360), FrameRate = 30 },
            CancellationToken.None
        );
        HttpMediaSession publishing = await WhipClient.PublishAsync(
            app.Services.GetRequiredService<IMediaSessionFactory>(),
            new Uri(Address(app), "/whip"),
            new MediaEndpoints { VideoSource = video },
            Loopback()
        );
        await using (publishing)
        {
            await publishing.Session.Connected.WaitAsync(TimeSpan.FromSeconds(20));
            await watch.UntilAsync(
                m => m.Frames >= 30,
                TimeSpan.FromSeconds(30),
                TestContext.CancellationToken
            );

            await publishing.Session.RestartTransportAsync();
            int atRestart = analyzer.Measure().Frames;
            await watch.UntilAsync(
                m => m.Frames >= atRestart + 60,
                TimeSpan.FromSeconds(30),
                TestContext.CancellationToken
            );

            Assert.AreEqual(TransportState.Connected, publishing.Session.State);
        }
    }

    [TestMethod]
    public void IceServerLinks_RoundTripServersWithCredentials()
    {
        IceServer[] servers =
        [
            new(["stun:stun.example.net:3478"]),
            new(
                ["turn:turn.example.net?transport=udp", "turns:turn.example.net"],
                "user, \"quoted\"",
                "p;a,ss"
            ),
        ];

        List<IceServer> parsed = IceServerLinks.Parse([
            string.Join(", ", IceServerLinks.Format(servers)),
            "<https://example.net>; rel=\"next\"",
        ]);

        Assert.HasCount(3, parsed);
        Assert.AreEqual("stun:stun.example.net:3478", parsed[0].Urls[0]);
        Assert.IsNull(parsed[0].Username);
        Assert.AreEqual("turns:turn.example.net", parsed[2].Urls[0]);
        Assert.AreEqual("user, \"quoted\"", parsed[2].Username);
        Assert.AreEqual("p;a,ss", parsed[2].Credential);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Options_AdvertisesTheHostsIceServers()
    {
        await using WebApplication app = await StartAsync(
            web => web.MapWhip("/whip", (_, _) => ValueTask.FromResult<HttpMediaSetup?>(null)),
            services =>
                services.AddSingleton<IIceServerProvider>(
                    new Agash.StreamTransport.Stun.StaticIceServerProvider([
                        new IceServer(["turn:relay.example.net"], "u", "p"),
                    ])
                )
        );
        using HttpClient http = new() { BaseAddress = Address(app) };

        using HttpResponseMessage options = await http.SendAsync(
            new HttpRequestMessage(HttpMethod.Options, "/whip")
        );
        List<IceServer> advertised = IceServerLinks.Parse(options.Headers.GetValues("Link"));

        Assert.HasCount(1, advertised);
        Assert.AreEqual("turn:relay.example.net", advertised[0].Urls[0]);
        Assert.AreEqual("u", advertised[0].Username);
    }

    private static MediaSessionOptions Loopback() =>
        MediaServices.Loopback(new MediaSessionOptions { VideoCodecs = [VideoCodecId.H264] });

    // An ordinary ASP.NET Core application on a loopback port, with the media services and the endpoints.
    private static double Percentile(double[] seconds, double p) =>
        seconds.Length == 0
            ? double.NaN
            : seconds.Order().ElementAt((int)Math.Min(seconds.Length - 1, p * seconds.Length))
                * 1000;

    private static double Mean(double[] seconds) =>
        seconds.Length == 0 ? double.NaN : seconds.Average() * 1000;

    private static async Task<WebApplication> StartAsync(
        Action<WebApplication> map,
        Action<IServiceCollection>? services = null
    )
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder
            .Services.AddStreamTransport()
            .AddStreamTransportWebRtc()
            .AddFFmpegCodecs()
            .AddOpusCodecs()
            .AddHttpMediaEndpoints();
        services?.Invoke(builder.Services);
        WebApplication app = builder.Build();
        map(app);
        await app.StartAsync();
        return app;
    }

    private static Uri Address(WebApplication app) =>
        new(
            app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.First()
        );

    // A video sink that shows frames 100 ms after it takes them, as a compositor or display does.
    private sealed class LatentVideoSink : IVideoSink
    {
        public static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(100);

        public VideoConstraints Constraints { get; } =
            VideoConstraints.Cpu(PixelFormat.Nv12, PixelFormat.I420);

        public TimeSpan? OutputLatency => Latency;

        public void OnFrame(in VideoFrame frame) { }
    }
}
