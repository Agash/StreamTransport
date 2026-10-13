using System.Collections.Concurrent;
using System.Net;
using Agash.StreamTransport.AspNetCore;
using Agash.StreamTransport.Codecs.FFmpeg;
using Agash.StreamTransport.Codecs.Opus;
using Agash.StreamTransport.Http;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.TestSignal;
using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.Ice;
using Agash.StreamTransport.WebRtc.Tests;
using Agash.StreamTransport.WebRtc.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Agash.StreamTransport.Tests;

// A WHIP publisher moving to another network while it publishes: the signalling is real HTTP against an
// ASP.NET Core host, and the media runs on an in-memory network whose interfaces the test changes.
[TestClass]
public sealed class WhipMobilityTests
{
    private static readonly IPAddress Wifi = IPAddress.Parse("10.0.0.1");
    private static readonly IPAddress Cellular = IPAddress.Parse("10.1.0.1");
    private static readonly IPAddress Server = IPAddress.Parse("10.0.0.2");

    // Wi-Fi goes and a modem comes up in one change, as when a backpack leaves a venue's network: the
    // publisher binds the modem, sends its candidate to the server by PATCH, and media carries on there.
    [TestMethod]
    [Timeout(90_000)]
    public async Task Publisher_OnANewInterface_PatchesItsCandidate_AndKeepsMediaFlowing()
    {
        InMemoryIceNetwork network = new();
        HashSet<IPAddress> gone = [];
        network.Drop = (from, to, _) =>
        {
            lock (gone)
            {
                return gone.Contains(from.Address) || gone.Contains(to.Address);
            }
        };
        List<IPAddress> interfaces = [Wifi];
        IIceSocketFactory publisherSockets = network.Factory(() =>
        {
            lock (interfaces)
            {
                return [.. interfaces];
            }
        });
        SimulatedNetworkMonitor monitor = new();
        TaskCompletionSource cellularPatched = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        AnalyzerWatch watch = new(new TestSignalAnalyzer());
        TestSignalAnalyzer analyzer = watch.Analyzer;

        await using WebApplication app = await StartAsync(
            monitor,
            web =>
            {
                web.Use(
                    async (context, next) =>
                    {
                        if (HttpMethods.IsPatch(context.Request.Method))
                        {
                            context.Request.EnableBuffering();
                            using StreamReader reader = new(context.Request.Body, leaveOpen: true);
                            string body = await reader.ReadToEndAsync(context.RequestAborted);
                            if (body.Contains($" {Cellular} ", StringComparison.Ordinal))
                            {
                                _ = cellularPatched.TrySetResult();
                            }

                            context.Request.Body.Position = 0;
                        }

                        await next(context);
                    }
                );
                web.MapWhip(
                    "/whip",
                    (_, _) =>
                        ValueTask.FromResult<HttpMediaSetup?>(
                            new HttpMediaSetup(
                                new MediaEndpoints { VideoSink = watch.WrapVideo() },
                                Options(network.Factory(Server))
                            )
                        )
                );
            }
        );

        TestSignalGenerator generator = new();
        using IVideoInput video = await new TestSignalVideoInputProvider(generator).OpenAsync(
            new VideoInputInfo("test", "signal", "Test signal", MediaInputKind.Generated, []),
            new VideoInputRequest { Size = new VideoSize(320, 240), FrameRate = 30 },
            CancellationToken.None
        );
        HttpMediaSession publishing = await WhipClient.PublishAsync(
            app.Services.GetRequiredService<IMediaSessionFactory>(),
            new Uri(Address(app), "/whip"),
            new MediaEndpoints { VideoSource = video },
            Options(publisherSockets)
        );
        await using (publishing)
        {
            await publishing.Session.Connected.WaitAsync(TimeSpan.FromSeconds(20));
            await watch.UntilAsync(m => m.Frames >= 30, TimeSpan.FromSeconds(30));

            lock (interfaces)
            {
                interfaces.Clear();
                interfaces.Add(Cellular);
            }

            lock (gone)
            {
                _ = gone.Add(Wifi);
            }

            monitor.Change();

            await cellularPatched.Task.WaitAsync(TimeSpan.FromSeconds(20));

            int atChange = analyzer.Measure().Frames;
            await watch.UntilAsync(m => m.Frames >= atChange + 60, TimeSpan.FromSeconds(30));
            Assert.AreEqual(TransportState.Connected, publishing.Session.State);
            Assert.IsEmpty(network.BoundOn(Wifi), "the socket on the network that went is closed.");
        }
    }

    private static MediaSessionOptions Options(
        IIceSocketFactory sockets,
        IceTimings? timings = null
    ) =>
        new()
        {
            VideoCodecs = [VideoCodecId.H264],
            Transport = new WebRtcTransportOptions
            {
                SocketFactory = sockets,
                IceTimings = timings,
            },
        };

    // Consent that lapses in a second and a half, so an outage outlasts it quickly.
    private static readonly IceTimings QuickConsent = IceTimings.Default with
    {
        ConsentTimeout = TimeSpan.FromMilliseconds(1500),
        StablePingInterval = TimeSpan.FromMilliseconds(200),
        WeakPingInterval = TimeSpan.FromMilliseconds(200),
        ReceivingTimeout = TimeSpan.FromMilliseconds(500),
    };

    // An outage longer than consent spends the credentials (RFC 7675 section 5.1): the publisher restarts
    // ICE by PATCH, and once the network is back media carries on under the new credentials.
    [TestMethod]
    [Timeout(90_000)]
    public async Task Publisher_AfterAnOutageLongerThanConsent_RestartsAndCarriesOn()
    {
        InMemoryIceNetwork network = new();
        bool down = false;
        network.Drop = (_, _, _) => Volatile.Read(ref down);
        TaskCompletionSource restarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AnalyzerWatch watch = new(new TestSignalAnalyzer());
        await using WebApplication app = await StartAsync(
            new SimulatedNetworkMonitor(),
            web =>
            {
                web.Use(
                    async (context, next) =>
                    {
                        if (HttpMethods.IsPatch(context.Request.Method))
                        {
                            context.Request.EnableBuffering();
                            using StreamReader reader = new(context.Request.Body, leaveOpen: true);
                            string body = await reader.ReadToEndAsync(context.RequestAborted);
                            if (body.Contains("a=ice-ufrag:", StringComparison.Ordinal))
                            {
                                restarted.TrySetResult();
                            }

                            context.Request.Body.Position = 0;
                        }

                        await next(context);
                    }
                );
                web.MapWhip(
                    "/whip",
                    (_, _) =>
                        ValueTask.FromResult<HttpMediaSetup?>(
                            new HttpMediaSetup(
                                new MediaEndpoints { VideoSink = watch.WrapVideo() },
                                Options(network.Factory(Server), QuickConsent)
                            )
                        )
                );
            }
        );

        TestSignalGenerator generator = new();
        using IVideoInput video = await new TestSignalVideoInputProvider(generator).OpenAsync(
            new VideoInputInfo("test", "signal", "Test signal", MediaInputKind.Generated, []),
            new VideoInputRequest { Size = new VideoSize(320, 240), FrameRate = 30 },
            CancellationToken.None
        );
        HttpMediaSession publishing = await WhipClient.PublishAsync(
            app.Services.GetRequiredService<IMediaSessionFactory>(),
            new Uri(Address(app), "/whip"),
            new MediaEndpoints { VideoSource = video },
            Options(network.Factory(Wifi), QuickConsent)
        );
        await using (publishing)
        {
            await publishing.Session.Connected.WaitAsync(TimeSpan.FromSeconds(20));
            await watch.UntilAsync(m => m.Frames >= 30, TimeSpan.FromSeconds(30));

            Volatile.Write(ref down, true);
            await restarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Volatile.Write(ref down, false);

            int atRestart = watch.Analyzer.Measure().Frames;
            await watch.UntilAsync(m => m.Frames >= atRestart + 60, TimeSpan.FromSeconds(30));
            Assert.AreEqual(TransportState.Connected, publishing.Session.State);
        }
    }

    private static async Task<WebApplication> StartAsync(
        INetworkMonitor monitor,
        Action<WebApplication> map
    )
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(monitor);
        builder
            .Services.AddStreamTransport()
            .AddStreamTransportWebRtc()
            .AddFFmpegCodecs()
            .AddOpusCodecs()
            .AddHttpMediaEndpoints();
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

    // The host's interfaces as far as mobility is told: a change is whatever the test says it is.
    private sealed class SimulatedNetworkMonitor : INetworkMonitor
    {
        public IReadOnlyList<NetworkPathInfo> Current { get; private set; } = [];

        public event Action<IReadOnlyList<NetworkPathInfo>>? NetworksChanged;

        public void Start() { }

        public void Change()
        {
            Current = [new NetworkPathInfo("wwan0", "wwan0", NetworkAdapterType.Cellular, true)];
            NetworksChanged?.Invoke(Current);
        }

        public void Dispose() { }
    }
}
