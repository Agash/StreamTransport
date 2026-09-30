using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Codecs.FFmpeg.Tests;

[TestClass]
public sealed class DecoderTests
{
    private const int FrameCount = 20;

    public static IEnumerable<object[]> Cases =>
        from backend in Enum.GetValues<DecoderBackend>()
        from codec in Enum.GetValues<VideoCodecId>()
        select new object[] { backend, codec };

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void Decode_ToSystemMemory_GivesThePicturesAndTimesEncoded(
        DecoderBackend backend,
        VideoCodecId codec
    )
    {
        List<(byte[] Data, MediaTimestamp Timestamp)> stream = Stream(codec);
        FFmpegVideoDecoderFactory factory = new(backend);
        var output = VideoConstraints.Cpu(PixelFormat.Nv12);
        if (factory.QueryCapabilities(new VideoCodecFormat(codec), output) is null)
        {
            Assert.Inconclusive($"{backend} cannot decode {codec} on this machine.");
        }

        using IVideoDecoder decoder = factory.Create(new VideoCodecFormat(codec), output);
        LumaRecorder recorder = new();
        foreach ((byte[] data, MediaTimestamp timestamp) in stream)
        {
            decoder.Decode(
                new EncodedVideoFrame(data, codec, keyframe: false, timestamp),
                recorder
            );
        }

        Assert.HasCount(FrameCount, recorder.Frames);
        for (int i = 0; i < FrameCount; i++)
        {
            Assert.AreEqual(Pictures.Timestamp(i), recorder.Frames[i].Timestamp);
            Assert.AreEqual(VideoStorageKind.Cpu, recorder.Frames[i].Storage);
            Assert.AreEqual(Pictures.MeanLuma(i), recorder.Frames[i].MeanLuma, 6.0, $"Frame {i}.");
        }
    }

    [TestMethod]
    public void Retain_DecodedFrame_KeepsItsPixelsAfterTheDecoderMovesOn()
    {
        List<(byte[] Data, MediaTimestamp Timestamp)> stream = Stream(VideoCodecId.H264);
        FFmpegVideoDecoderFactory factory = new(DecoderBackend.Software);
        using IVideoDecoder decoder = factory.Create(
            new VideoCodecFormat(VideoCodecId.H264),
            VideoConstraints.Cpu(PixelFormat.I420)
        );
        Keeper keeper = new();
        foreach ((byte[] data, MediaTimestamp timestamp) in stream)
        {
            decoder.Decode(
                new EncodedVideoFrame(data, VideoCodecId.H264, false, timestamp),
                keeper
            );
        }

        Assert.HasCount(FrameCount, keeper.Leases);
        for (int i = 0; i < FrameCount; i++)
        {
            using VideoFrameLease lease = keeper.Leases[i];
            Assert.AreEqual(
                Pictures.MeanLuma(i),
                LumaRecorder.MeanLuma(lease.Frame),
                6.0,
                $"Frame {i}."
            );
        }
    }

    // GPU frames from the decoder straight into an encoder on the same GPU, and the result decoded in
    // software: a transcode that never leaves the GPU.
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [DataRow(DecoderBackend.D3D12, VideoStorageKind.D3D12, EncoderBackend.D3D12)]
    [DataRow(DecoderBackend.D3D11, VideoStorageKind.D3D11, EncoderBackend.Nvenc)]
    [DataRow(DecoderBackend.D3D11, VideoStorageKind.D3D11, EncoderBackend.Amf)]
    public void Transcode_OnTheGpu_KeepsThePictures(
        DecoderBackend decoderBackend,
        VideoStorageKind storage,
        EncoderBackend encoderBackend
    )
    {
        const VideoCodecId codec = VideoCodecId.H265;
        FFmpegVideoEncoderFactory encoders = new(encoderBackend);
        VideoEncoderInfo? encoderInfo = encoders.QueryCapabilities(
            new VideoCodecFormat(codec),
            device: null
        );
        if (encoderInfo is null || !encoderInfo.Input.Storages.Contains(storage))
        {
            Assert.Inconclusive($"{encoderBackend} does not take {storage} {codec} frames here.");
        }

        GpuIdentity gpu = encoderInfo.Input.Device!.Value;
        VideoConstraints gpuOutput = new([storage], [PixelFormat.Nv12], gpu);
        FFmpegVideoDecoderFactory decoders = new(decoderBackend);
        VideoDecoderInfo? decoderInfo = decoders.QueryCapabilities(
            new VideoCodecFormat(codec),
            gpuOutput
        );
        if (decoderInfo is null || !decoderInfo.Output.Storages.Contains(storage))
        {
            Assert.Inconclusive(
                $"{decoderBackend} cannot decode {codec} into {storage} on that GPU."
            );
        }

        using IVideoDecoder decoder = decoders.Create(new VideoCodecFormat(codec), gpuOutput);
        using IVideoEncoder encoder = encoders.Create(
            new VideoEncoderConfiguration(
                new VideoCodecFormat(codec),
                Pictures.Size,
                new RateTarget(4_000_000, 30)
            ),
            gpu
        );
        Collector collector = new();
        Relay relay = new(encoder, collector);
        foreach ((byte[] data, MediaTimestamp timestamp) in Stream(codec))
        {
            decoder.Decode(new EncodedVideoFrame(data, codec, false, timestamp), relay);
        }

        encoder.Flush(collector);
        Assert.AreEqual(storage, relay.Storage);
        var decoded = Reference.Decode(codec, collector.Units.Select(static u => u.Data));
        Assert.HasCount(FrameCount, decoded);
        for (int i = 0; i < FrameCount; i++)
        {
            Assert.AreEqual(Pictures.MeanLuma(i), decoded[i].MeanLuma, 8.0, $"Frame {i}.");
        }
    }

    // The test stream: the moving gradient, encoded in software.
    private static List<(byte[] Data, MediaTimestamp Timestamp)> Stream(VideoCodecId codec)
    {
        VideoEncoderConfiguration configuration = new(
            new VideoCodecFormat(codec),
            Pictures.Size,
            new RateTarget(4_000_000, 30)
        );
        FFmpegVideoEncoderFactory factory =
            FFmpegVideoEncoderFactory
                .CreateAll()
                .OrderBy(static f => f.IsHardwareAccelerated)
                .FirstOrDefault(f =>
                    f.QueryCapabilities(configuration.Format, device: null) is { } info
                    && info.Input.PixelFormats.Any(static p =>
                        p is PixelFormat.Nv12 or PixelFormat.I420
                    )
                )
            ?? throw new AssertInconclusiveException($"Nothing on this machine encodes {codec}.");
        using IVideoEncoder encoder = factory.Create(configuration, device: null);
        PixelFormat format = encoder.Info.Input.PixelFormats.First(static f =>
            f is PixelFormat.Nv12 or PixelFormat.I420
        );
        Collector collector = new();
        for (int i = 0; i < FrameCount; i++)
        {
            byte[] pixels = Pictures.Make(format, i);
            VideoFrame frame = Pictures.CpuFrame(format, pixels, i);
            encoder.Encode(in frame, new EncodeRequest(Keyframe: i == 0), collector);
        }

        encoder.Flush(collector);
        return [.. collector.Units.Select(static u => (u.Data, u.Timestamp))];
    }

    private sealed class LumaRecorder : IVideoFrameConsumer
    {
        public List<(
            MediaTimestamp Timestamp,
            VideoStorageKind Storage,
            double MeanLuma
        )> Frames { get; } = [];

        public void OnFrame(in VideoFrame frame) =>
            Frames.Add((frame.Timestamp, frame.Storage.Kind, MeanLuma(frame)));

        public static double MeanLuma(in VideoFrame frame)
        {
            _ = frame.Storage.TryGetValue(out CpuImage image);
            int stride = image.Planes[0].Stride;
            ReadOnlySpan<byte> luma = frame.GetPlane(0);
            long sum = 0;
            for (int y = 0; y < frame.Format.VisibleRect.Height; y++)
            {
                foreach (byte value in luma.Slice(y * stride, frame.Format.VisibleRect.Width))
                {
                    sum += value;
                }
            }

            return sum / (double)(frame.Format.VisibleRect.Width * frame.Format.VisibleRect.Height);
        }
    }

    private sealed class Keeper : IVideoFrameConsumer
    {
        public List<VideoFrameLease> Leases { get; } = [];

        public void OnFrame(in VideoFrame frame) => Leases.Add(frame.Retain());
    }

    private sealed class Relay(IVideoEncoder encoder, Collector collector) : IVideoFrameConsumer
    {
        public VideoStorageKind Storage { get; private set; }

        public void OnFrame(in VideoFrame frame)
        {
            Storage = frame.Storage.Kind;
            encoder.Encode(in frame, default, collector);
        }
    }
}
