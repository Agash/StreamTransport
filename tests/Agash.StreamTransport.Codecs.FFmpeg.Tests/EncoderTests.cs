using System.Collections.Immutable;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Codecs.FFmpeg.Tests;

// Every backend this machine can run, for every codec it offers. A backend the machine lacks (no
// such GPU, driver or runtime) reports no capabilities and its cases are inconclusive.
[TestClass]
public sealed class EncoderTests
{
    private const int FrameCount = 30;

    public static IEnumerable<object[]> Cases =>
        from backend in Enum.GetValues<EncoderBackend>()
        from codec in VideoCodecId.BuiltIn
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
    [DataRow(1_000_000L, 960_000L, false)]
    [DataRow(1_000_000L, 840_000L, true)]
    [DataRow(1_000_000L, 1_250_000L, false)]
    [DataRow(1_000_000L, 1_310_000L, true)]
    public void WorthReopening_FollowsFallsSoonerThanRises(
        long opened,
        long target,
        bool expected
    ) => Assert.AreEqual(expected, FFmpegVideoEncoder.WorthReopening(opened, target));

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

    public static IEnumerable<object[]> ProfileCases =>
        from backend in Enum.GetValues<EncoderBackend>()
        from profile in new[] { "42e01f", "640c1f" }
        select new object[] { backend, profile };

    // A receiver's declared H.264 profile bounds what the encoder writes: the SPS of its first keyframe
    // names that profile or one the receiver's includes. An encoder that cannot keep to it is not offered.
    [TestMethod]
    [DynamicData(nameof(ProfileCases))]
    public void Encode_H264ToADeclaredProfile_WritesAProfileTheReceiverTakes(
        EncoderBackend backend,
        string declared
    )
    {
        FFmpegVideoEncoderFactory factory = new(backend);
        _ = Available(factory, VideoCodecId.H264);
        VideoCodecFormat format = new(
            VideoCodecId.H264,
            ImmutableSortedDictionary<string, string>.Empty.Add(
                H264ProfileLevelId.ParameterName,
                declared
            )
        );
        Assert.IsTrue(H264ProfileLevelId.TryParse(declared, out H264ProfileLevelId accepted));
        if (factory.QueryCapabilities(format, device: null) is not { } info)
        {
            // Some lack Constrained Baseline (D3D12 has no Baseline; Vulkan drivers may not offer it), but
            // every H.264 encoder writes something within Constrained High.
            Assert.AreEqual(
                H264Profile.ConstrainedBaseline,
                accepted.Profile,
                $"{backend} refused {declared}."
            );
            return;
        }

        PixelFormat pixelFormat = info.Input.PixelFormats.Contains(PixelFormat.Nv12)
            ? PixelFormat.Nv12
            : PixelFormat.I420;
        using IVideoEncoder encoder = factory.Create(
            new VideoEncoderConfiguration(format, Pictures.Size, new RateTarget(2_000_000, 30)),
            device: null
        );
        Collector collector = new();
        for (int i = 0; i < 3; i++)
        {
            VideoFrame frame = Pictures.CpuFrame(pixelFormat, Pictures.Make(pixelFormat, i), i);
            encoder.Encode(in frame, new EncodeRequest(Keyframe: i == 0), collector);
        }

        encoder.Flush(collector);
        // Every decoder here reads the same pictures from it: a stream whose parameter sets misdescribe
        // what was coded decodes differently in each.
        foreach (DecoderBackend decoderBackend in Enum.GetValues<DecoderBackend>())
        {
            var decoders = new FFmpegVideoDecoderFactory(decoderBackend);
            var output = VideoConstraints.Cpu(PixelFormat.Nv12);
            if (decoders.QueryCapabilities(format, output) is null)
            {
                continue;
            }

            using IVideoDecoder decoder = decoders.Create(format, output);
            MeanLumas lumas = new();
            foreach ((byte[] data, bool keyframe, MediaTimestamp timestamp) in collector.Units)
            {
                decoder.Decode(
                    new EncodedVideoFrame(data, VideoCodecId.H264, keyframe, timestamp),
                    lumas
                );
            }

            Assert.HasCount(
                3,
                lumas.Values,
                $"{decoderBackend} decoding {info.ImplementationName}"
            );
            for (int i = 0; i < lumas.Values.Count; i++)
            {
                Assert.AreEqual(
                    Pictures.MeanLuma(i),
                    lumas.Values[i],
                    6.0,
                    $"{decoderBackend} decoding {info.ImplementationName}, frame {i}"
                );
            }
        }

        H264Profile written = SpsProfile(collector.Units[0].Data);
        H264Profile[] taken =
            accepted.Profile == H264Profile.ConstrainedBaseline
                ? [H264Profile.ConstrainedBaseline]
                :
                [
                    H264Profile.ConstrainedBaseline,
                    H264Profile.Main,
                    H264Profile.ConstrainedHigh,
                    H264Profile.High,
                ];
        CollectionAssert.Contains(
            taken,
            written,
            $"{info.ImplementationName} wrote {written} for {accepted.Profile}."
        );
    }

    private sealed class MeanLumas : IVideoFrameConsumer
    {
        public List<double> Values { get; } = [];

        public void OnFrame(in VideoFrame frame)
        {
            _ = frame.Storage.TryGetValue(out CpuImage image);
            int stride = image.Planes[0].Stride;
            ReadOnlySpan<byte> luma = frame.GetPlane(0);
            long sum = 0;
            int width = frame.Format.VisibleRect.Width;
            int height = frame.Format.VisibleRect.Height;
            for (int y = 0; y < height; y++)
            {
                foreach (byte value in luma.Slice(y * stride, width))
                {
                    sum += value;
                }
            }

            Values.Add(sum / (double)(width * height));
        }
    }

    // The profile an Annex B access unit's SPS names.
    private static H264Profile SpsProfile(byte[] unit)
    {
        for (int i = 0; i + 6 < unit.Length; i++)
        {
            if (unit[i] == 0 && unit[i + 1] == 0 && unit[i + 2] == 1 && (unit[i + 3] & 0x1F) == 7)
            {
                string id = Convert.ToHexStringLower(unit.AsSpan(i + 4, 3));
                Assert.IsTrue(H264ProfileLevelId.TryParse(id, out H264ProfileLevelId profile), id);
                return profile.Profile;
            }
        }

        Assert.Fail("The keyframe carries no SPS.");
        return default;
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
