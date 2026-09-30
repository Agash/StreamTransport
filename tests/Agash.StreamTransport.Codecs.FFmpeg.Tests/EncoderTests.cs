using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Codecs.FFmpeg.Tests;

// Every backend this machine can run, for every codec it offers. A backend the machine lacks (no
// such GPU, driver or runtime) reports no capabilities and its cases are inconclusive, not passed.
[TestClass]
public sealed class EncoderTests
{
    private const int FrameCount = 30;

    public static IEnumerable<object[]> Cases =>
        from backend in Enum.GetValues<EncoderBackend>()
        from codec in Enum.GetValues<VideoCodecId>()
        select new object[] { backend, codec };

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void Encode_SystemMemory_DecodesToThePicturesSent(
        EncoderBackend backend,
        VideoCodecId codec
    )
    {
        FFmpegVideoEncoderFactory factory = new(backend);
        VideoEncoderInfo info = Available(factory, codec);
        PixelFormat format =
            info.Input.PixelFormats.Contains(PixelFormat.Nv12) ? PixelFormat.Nv12
            : info.Input.PixelFormats.Contains(PixelFormat.I420) ? PixelFormat.I420
            : Inconclusive<PixelFormat>($"{info.ImplementationName} takes neither NV12 nor I420.");

        using IVideoEncoder encoder = factory.Create(Configuration(codec, 4_000_000), device: null);
        Collector collector = new();
        for (int i = 0; i < FrameCount; i++)
        {
            byte[] pixels = Pictures.Make(format, i);
            VideoFrame frame = Pictures.CpuFrame(format, pixels, i);
            encoder.Encode(in frame, new EncodeRequest(Keyframe: i == 0), collector);
        }

        encoder.Flush(collector);

        Assert.IsTrue(collector.Units[0].Keyframe, "The first access unit is not a keyframe.");
        CollectionAssert.AreEqual(
            Enumerable.Range(0, FrameCount).Select(Pictures.Timestamp).ToList(),
            collector.Units.Select(static u => u.Timestamp).ToList(),
            "Access units do not carry the capture times of the frames, in order."
        );
        var decoded = Reference.Decode(codec, collector.Units.Select(static u => u.Data));
        Assert.HasCount(FrameCount, decoded);
        for (int i = 0; i < FrameCount; i++)
        {
            Assert.AreEqual(Pictures.Width, decoded[i].Width);
            Assert.AreEqual(Pictures.Height, decoded[i].Height);
            Assert.AreEqual(
                Pictures.MeanLuma(i),
                decoded[i].MeanLuma,
                6.0,
                $"Frame {i} decodes to other content than was sent."
            );
        }
    }

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void Reconfigure_LowerRate_ShrinksTheStream(EncoderBackend backend, VideoCodecId codec)
    {
        FFmpegVideoEncoderFactory factory = new(backend);
        VideoEncoderInfo info = Available(factory, codec);
        PixelFormat format = info.Input.PixelFormats[0];
        if (format is not (PixelFormat.Nv12 or PixelFormat.I420))
        {
            Assert.Inconclusive($"{info.ImplementationName} takes {format}.");
        }

        using IVideoEncoder encoder = factory.Create(Configuration(codec, 4_000_000), device: null);
        Collector collector = new();
        Random noise = new(7);
        void Send(int index)
        {
            // Moving detail with grain: more than a low rate can carry at full quality, so the rate
            // decides the size, but compressible enough that every encoder can reach a low rate.
            byte[] pixels = Pictures.Make(format, index);
            Span<byte> luma = pixels.AsSpan(0, Pictures.Width * Pictures.Height);
            for (int p = 0; p < luma.Length; p++)
            {
                luma[p] = (byte)Math.Clamp(luma[p] + noise.Next(-24, 25), 0, 255);
            }

            VideoFrame frame = Pictures.CpuFrame(format, pixels, index);
            encoder.Encode(in frame, default, collector);
        }

        for (int i = 0; i < 60; i++)
        {
            Send(i);
        }

        int before = collector.Units.Count;
        encoder.Reconfigure(new RateTarget(500_000, 30));
        for (int i = 60; i < 120; i++)
        {
            Send(i);
        }

        encoder.Flush(collector);
        // Skip the frames right after the change: a reopened encoder starts with a keyframe.
        long high = collector.Bytes(before - 30, before);
        long low = collector.Bytes(before + 30, before + 60);
        Assert.IsGreaterThan(
            3.0,
            high / (double)low,
            $"{info.ImplementationName}: {high} bytes per second before, {low} after."
        );
    }

    // Congestion control moves the rate every few frames, down and back up; every step must be taken
    // without an error from the encoder.
    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void Reconfigure_StepsLikeCongestionControl_KeepsProducing(
        EncoderBackend backend,
        VideoCodecId codec
    )
    {
        FFmpegVideoEncoderFactory factory = new(backend);
        VideoEncoderInfo info = Available(factory, codec);
        PixelFormat format = info.Input.PixelFormats[0];
        if (format is not (PixelFormat.Nv12 or PixelFormat.I420))
        {
            Assert.Inconclusive($"{info.ImplementationName} takes {format}.");
        }

        // Rates for this picture size: up to about 0.9 bits per pixel per frame, well past what a real
        // stream uses (1080p at 12 Mb/s is 0.2). Far beyond that, a single frame outgrows the output
        // buffer NVENC sizes from the resolution.
        long[] steps = [1_500_000, 300_000, 800_000, 100_000, 2_000_000, 500_000];
        using IVideoEncoder encoder = factory.Create(Configuration(codec, steps[0]), device: null);
        Collector collector = new();
        Random noise = new(3);
        int index = 0;
        foreach (long rate in steps)
        {
            encoder.Reconfigure(new RateTarget(rate, 30));
            for (int i = 0; i < 5; i++, index++)
            {
                // Noise fills the encoder's rate-control buffer, the state a rate drop meets in a
                // congested moment.
                byte[] pixels = Pictures.Make(format, index);
                noise.NextBytes(pixels.AsSpan(0, Pictures.Width * Pictures.Height));
                VideoFrame frame = Pictures.CpuFrame(format, pixels, index);
                encoder.Encode(in frame, default, collector);
            }
        }

        encoder.Flush(collector);
        // An encoder may skip frames to hold a sudden low rate, as VideoToolbox does after the keyframe
        // a reopen starts with; what it may not do is stop, so every step must still produce frames.
        for (int step = 0; step < steps.Length; step++)
        {
            int first = step * 5;
            int produced = collector.Units.Count(u =>
                Enumerable.Range(first, 5).Any(i => u.Timestamp == Pictures.Timestamp(i))
            );
            Assert.IsGreaterThan(
                0,
                produced,
                $"{info.ImplementationName} produced nothing at {steps[step]} b/s."
            );
        }
    }

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void Encode_KeyframeRequest_IsHonouredAtThatFrame(
        EncoderBackend backend,
        VideoCodecId codec
    )
    {
        FFmpegVideoEncoderFactory factory = new(backend);
        VideoEncoderInfo info = Available(factory, codec);
        PixelFormat format = info.Input.PixelFormats[0];
        if (format is not (PixelFormat.Nv12 or PixelFormat.I420))
        {
            Assert.Inconclusive($"{info.ImplementationName} takes {format}.");
        }

        using IVideoEncoder encoder = factory.Create(Configuration(codec, 2_000_000), device: null);
        Collector collector = new();
        for (int i = 0; i < 12; i++)
        {
            byte[] pixels = Pictures.Make(format, i);
            VideoFrame frame = Pictures.CpuFrame(format, pixels, i);
            encoder.Encode(in frame, new EncodeRequest(Keyframe: i is 0 or 8), collector);
        }

        encoder.Flush(collector);
        var keyframes = collector
            .Units.Select(static (u, i) => (u, i))
            .Where(static p => p.u.Keyframe)
            .Select(static p => p.i)
            .ToList();
        CollectionAssert.AreEqual(
            new[] { 0, 8 },
            keyframes,
            $"{info.ImplementationName} made keyframes at [{string.Join(", ", keyframes)}] of {collector.Units.Count}."
        );
    }

    [TestMethod]
    public void CreateAll_OrdersBackendsByRank_ModernFirst()
    {
        var factories = FFmpegVideoEncoderFactory.CreateAll();

        Assert.IsNotEmpty(factories);
        CollectionAssert.AreEqual(
            factories.Select(static f => f.Rank).OrderByDescending(static r => r).ToList(),
            factories.Select(static f => f.Rank).ToList()
        );
        Assert.AreEqual(EncoderBackend.Software, factories[^1].Backend);
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual(EncoderBackend.D3D12, factories[0].Backend);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.AreEqual(EncoderBackend.Vulkan, factories[0].Backend);
        }
    }

    private static VideoEncoderConfiguration Configuration(
        VideoCodecId codec,
        long bitsPerSecond
    ) => new(new VideoCodecFormat(codec), Pictures.Size, new RateTarget(bitsPerSecond, 30));

    private static VideoEncoderInfo Available(
        FFmpegVideoEncoderFactory factory,
        VideoCodecId codec
    ) =>
        factory.QueryCapabilities(new VideoCodecFormat(codec), device: null)
        ?? Inconclusive<VideoEncoderInfo>(
            $"{factory.Backend} cannot encode {codec} on this machine."
        );

    private static T Inconclusive<T>(string reason)
    {
        Assert.Inconclusive(reason);
        return default!;
    }
}
