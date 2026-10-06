using System.Net;
using System.Text;
using Agash.StreamTransport.AspNetCore;
using Agash.StreamTransport.Codecs.FFmpeg;
using Agash.StreamTransport.Codecs.Opus;
using Agash.StreamTransport.Http;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.TestSignal;
using Agash.StreamTransport.WebRtc.DependencyInjection;
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
    [TestMethod]
    [Timeout(60_000)]
    public async Task Whip_PublishedTestSignal_ArrivesAndIsInSync()
    {
        TestSignalAnalyzer analyzer = new();
        int ended = 0;
        await using WebApplication app = await StartAsync(web =>
            web.MapWhip(
                "/whip/{room}",
                (context, _) =>
                    ValueTask.FromResult<HttpMediaSetup?>(
                        (string?)context.Request.RouteValues["room"] == "studio"
                            ? new HttpMediaSetup(
                                new MediaEndpoints
                                {
                                    VideoSink = analyzer.WrapVideo(),
                                    AudioSink = analyzer.WrapAudio(),
                                },
                                Loopback()
                            )
                            {
                                Ended = () =>
                                {
                                    Interlocked.Increment(ref ended);
                                    return ValueTask.CompletedTask;
                                },
                            }
                            : null
                    )
            )
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
            while (analyzer.Measure().Pairs < 1)
            {
                await Task.Delay(200);
            }

            analyzer.Reset();
            while (analyzer.Measure().Pairs < 3)
            {
                await Task.Delay(200);
            }
        }

        for (int wait = 0; wait < 50 && Volatile.Read(ref ended) == 0; wait++)
        {
            await Task.Delay(100);
        }

        TestSignalMeasurement measured = analyzer.Measure();
        Assert.IsLessThan(60, Math.Abs(measured.MeanOffset.TotalMilliseconds), measured.ToString());
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
        TestSignalAnalyzer analyzer = new();

        HttpMediaSession playing = await WhepClient.PlayAsync(
            app.Services.GetRequiredService<IMediaSessionFactory>(),
            new Uri(Address(app), "/whep"),
            new MediaEndpoints
            {
                VideoSink = analyzer.WrapVideo(),
                AudioSink = analyzer.WrapAudio(),
            },
            Loopback()
        );
        await using (playing)
        {
            await playing.Session.Connected.WaitAsync(TimeSpan.FromSeconds(20));
            while (analyzer.Measure().Pairs < 2)
            {
                await Task.Delay(200);
            }
        }

        Assert.IsGreaterThan(30, analyzer.Measure().Frames);
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

        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, notSdp.StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, options.StatusCode);
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, patch.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, delete.StatusCode);
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
}
