using System.Collections.Immutable;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.TestSignal;
using Microsoft.Extensions.DependencyInjection;

namespace Agash.StreamTransport.Tests;

[TestClass]
public sealed class MediaDevicesTests
{
    [TestMethod]
    public async Task HostProvider_RegisteredInDi_IsListedAndOpenedBySpec()
    {
        ServiceCollection services = new();
        services.AddStreamTransport();
        services.AddSingleton<IVideoInputProvider>(new FakeVideoProvider("ndi", 10, [("Studio A", MediaInputKind.Network, null)]));
        services.AddSingleton<IVideoOutputProvider, FakeVideoOutputProvider>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        MediaDevices devices = provider.GetRequiredService<MediaDevices>();

        ImmutableArray<VideoInputInfo> inputs = await devices.GetVideoInputsAsync();
        using IVideoInput opened = await devices.OpenVideoInputAsync("ndi:Studio A");
        using IVideoOutput output = await devices.CreateVideoOutputAsync("ndi", "Guest");

        Assert.IsTrue(inputs.Any(static i => i.Provider == "ndi" && i.Name == "Studio A"));
        Assert.IsTrue(inputs.Any(static i => i.Provider == "test"), "the test signal is registered too");
        Assert.AreEqual("Studio A", opened.Info.Name);
        Assert.AreEqual("Guest", output.Name);
    }

    [TestMethod]
    public async Task OpenVideoInput_ByKindNameOrProvider_FindsTheInput()
    {
        MediaDevices devices = Devices(
            new FakeVideoProvider("cams", 100, [("Desk", MediaInputKind.Camera, null), ("Overhead", MediaInputKind.Camera, null)]),
            new FakeVideoProvider("apps", 50, [("OBS", MediaInputKind.Application, null)])
        );

        using IVideoInput byKind = await devices.OpenVideoInputAsync("camera");
        using IVideoInput byName = await devices.OpenVideoInputAsync("overhead");
        using IVideoInput byProvider = await devices.OpenVideoInputAsync("apps");

        Assert.AreEqual("Desk", byKind.Info.Name);
        Assert.AreEqual("Overhead", byName.Info.Name);
        Assert.AreEqual("OBS", byProvider.Info.Name);
    }

    [TestMethod]
    public async Task OneDevice_TwoProviders_IsListedOnceByTheBest()
    {
        MediaDevices devices = Devices(
            new FakeVideoProvider("native", 100, [("Cam", MediaInputKind.Camera, "/dev/video0")]),
            new FakeVideoProvider("graph", 50, [("Cam (graph)", MediaInputKind.Camera, "/dev/video0")]),
            new FakeVideoProvider("fallback", 10, [("Cam", MediaInputKind.Camera, null)])
        );

        ImmutableArray<VideoInputInfo> inputs = await devices.GetVideoInputsAsync();

        Assert.HasCount(1, inputs);
        Assert.AreEqual("native", inputs[0].Provider);
    }

    [TestMethod]
    public async Task Open_FailingProvider_FallsBackToTheNextThatHasTheDevice()
    {
        var native = new FakeVideoProvider("native", 100, [("Cam", MediaInputKind.Camera, "/dev/video0")]) { Fails = true };
        var fallback = new FakeVideoProvider("fallback", 10, [("Cam", MediaInputKind.Camera, null)]);
        MediaDevices devices = Devices(native, fallback);

        using IVideoInput opened = await devices.OpenVideoInputAsync("Cam");

        Assert.AreEqual("fallback", opened.Info.Provider);
    }

    [TestMethod]
    public async Task Open_Unknown_Throws()
    {
        MediaDevices devices = Devices(new FakeVideoProvider("cams", 100, [("Desk", MediaInputKind.Camera, null)]));

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => devices.OpenVideoInputAsync("nothing"));
    }

    [TestMethod]
    public void Choose_PrefersTheRequestedSizeThenRateThenCheapFormat()
    {
        ImmutableArray<VideoInputMode> modes =
        [
            new(PixelFormat.Yuy2, new VideoSize(1920, 1080), 30),
            new(PixelFormat.Nv12, new VideoSize(1920, 1080), 30),
            new(PixelFormat.Nv12, new VideoSize(1920, 1080), 60),
            new(PixelFormat.Yuy2, new VideoSize(1280, 720), 60),
        ];

        Assert.AreEqual(modes[2], new VideoInputRequest().Choose(modes));
        Assert.AreEqual(modes[1], new VideoInputRequest { FrameRate = 30 }.Choose(modes));
        Assert.AreEqual(modes[0], new VideoInputRequest { FrameRate = 30, PixelFormat = PixelFormat.Yuy2 }.Choose(modes));
        Assert.AreEqual(modes[3], new VideoInputRequest { Size = new VideoSize(1280, 720) }.Choose(modes));
    }

    private static MediaDevices Devices(params IVideoInputProvider[] providers) => new(providers, [], [], []);

    private sealed class FakeVideoProvider(string name, int rank, (string Name, MediaInputKind Kind, string? Key)[] inputs)
        : IVideoInputProvider
    {
        public bool Fails { get; init; }

        public string Name => name;

        public int Rank => rank;

        public ValueTask<ImmutableArray<VideoInputInfo>> GetInputsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<ImmutableArray<VideoInputInfo>>(
                [.. inputs.Select(i => new VideoInputInfo(name, i.Name, i.Name, i.Kind, []) { DeviceKey = i.Key })]
            );

        public ValueTask<IVideoInput> OpenAsync(VideoInputInfo input, VideoInputRequest request, CancellationToken cancellationToken) =>
            Fails
                ? throw new IOException("The device is busy.")
                : ValueTask.FromResult<IVideoInput>(new VideoInput(new NoFrames(), input));
    }

    private sealed class FakeVideoOutputProvider : IVideoOutputProvider
    {
        public string Name => "ndi";

        public ValueTask<IVideoOutput> CreateAsync(string name, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IVideoOutput>(new VideoOutput(new NullSink(), Name, name));
    }

    private sealed class NoFrames : IVideoSource
    {
        public IDisposable Connect(IVideoFrameConsumer consumer, VideoConstraints constraints) => new Nothing();
    }

    private sealed class NullSink : IVideoSink
    {
        public VideoConstraints Constraints { get; } = VideoConstraints.Cpu(PixelFormat.Nv12);

        public void OnFrame(in VideoFrame frame) { }
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose() { }
    }
}

[TestClass]
public sealed class TestSignalTests
{
    [TestMethod]
    [Timeout(20_000)]
    public async Task Signal_StraightIntoTheAnalyzer_IsAlignedAndQuick()
    {
        var generator = new TestSignalGenerator();
        var analyzer = new TestSignalAnalyzer();
        using IVideoInput video = await new TestSignalVideoInputProvider(generator).OpenAsync(
            new VideoInputInfo("test", "signal", "Test signal", MediaInputKind.Generated, []),
            new VideoInputRequest { Size = new VideoSize(640, 360), FrameRate = 30 },
            CancellationToken.None
        );
        using IAudioInput audio = await new TestSignalAudioInputProvider(generator).OpenAsync(
            new AudioInputInfo("test", "signal", "Test signal", MediaInputKind.Generated),
            CancellationToken.None
        );

        using (video.Connect(analyzer.WrapVideo(), VideoConstraints.Cpu(PixelFormat.Nv12)))
        using (audio.Connect(analyzer.WrapAudio()))
        {
            while (analyzer.Measure().Pairs < 3)
            {
                await Task.Delay(100);
            }
        }

        TestSignalMeasurement measured = analyzer.Measure();
        // A frame lands up to one period after its click's sample, and timers add a few milliseconds.
        Assert.IsLessThan(45, Math.Abs(measured.MeanOffset.TotalMilliseconds), measured.ToString());
        Assert.IsLessThan(45, measured.MeanLatency.TotalMilliseconds, measured.ToString());
        Assert.IsGreaterThan(-5, measured.MinLatency.TotalMilliseconds, "the barcode reads the wall time it was made at");
    }

    [TestMethod]
    public void Barcode_RoundTripsTheWallTime()
    {
        var generator = new TestSignalGenerator();
        VideoSize size = new(640, 360);
        byte[] picture = new byte[size.Width * size.Height * 3 / 2];
        MediaTime time = generator.Origin + TimeSpan.FromMilliseconds(1234);

        generator.Draw(picture.AsSpan(0, size.Width * size.Height), picture.AsSpan(size.Width * size.Height), size, time, TimeSpan.FromMilliseconds(33));
        DateTimeOffset read = TestSignalGenerator.ReadBarcode(picture, size.Width, size, DateTimeOffset.UtcNow);

        Assert.AreEqual(generator.WallTime(time).ToUnixTimeMilliseconds(), read.ToUnixTimeMilliseconds());
    }
}

[TestClass]
public sealed class FrameRateMeterTests
{
    [TestMethod]
    public void Observe_SixtyFramesASecond_MeasuresSixtyAfterASecond()
    {
        var meter = new Agash.StreamTransport.Streams.FrameRateMeter(30);
        for (int i = 0; i < 30; i++)
        {
            meter.Observe(new MediaTime(i * 16_666_667L));
        }

        Assert.AreEqual(30, meter.FramesPerSecond, "half a second is not enough to judge");
        for (int i = 30; i < 90; i++)
        {
            meter.Observe(new MediaTime(i * 16_666_667L));
        }

        Assert.AreEqual(60, meter.FramesPerSecond, 0.5);
        Assert.IsTrue(Agash.StreamTransport.Streams.FrameRateMeter.Moved(30, meter.FramesPerSecond));
        Assert.IsFalse(Agash.StreamTransport.Streams.FrameRateMeter.Moved(59, meter.FramesPerSecond));
    }
}
