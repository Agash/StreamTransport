using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Codecs.FFmpeg.Tests;

[TestClass]
public sealed class ProcessorTests
{
    private static readonly FFmpegVideoProcessorFactory Factory = new();
    private static readonly VideoStreamDescription I420Input = new(
        VideoStorageKind.Cpu,
        PixelFormat.I420,
        Pictures.Size
    );

    [TestMethod]
    public void Process_I420ToHalfSizeNv12_KeepsThePicture()
    {
        using IVideoProcessor processor = Factory.Create(
            I420Input,
            new VideoProcessing(VideoConstraints.Cpu(PixelFormat.Nv12), new VideoSize(160, 120))
        );
        Recorder recorder = new();
        byte[] pixels = Pictures.Make(PixelFormat.I420, 3);

        processor.Process(Pictures.CpuFrame(PixelFormat.I420, pixels, 3), recorder);

        Assert.AreEqual(new VideoFormat(PixelFormat.Nv12, 160, 120), recorder.Format);
        Assert.AreEqual(Pictures.Timestamp(3), recorder.Timestamp);
        Assert.AreEqual(Pictures.MeanLuma(3), recorder.MeanLuma, 2.0);
        Assert.AreEqual(128, recorder.FirstChroma, 1);
    }

    [TestMethod]
    public void Process_VideoRangeToFullRange_StretchesTheLevels()
    {
        using IVideoProcessor processor = Factory.Create(
            I420Input,
            new VideoProcessing(
                VideoConstraints.Cpu(PixelFormat.Nv12),
                Color: VideoColor.Bt709 with
                {
                    Range = ColorRange.Full,
                }
            )
        );
        Recorder recorder = new();

        processor.Process(Flat(PixelFormat.I420, luma: 16), recorder);
        Assert.AreEqual(0, recorder.FirstLuma, 1, "video-range black is full-range 0");
        Assert.AreEqual(ColorRange.Full, recorder.Color.Range);

        processor.Process(Flat(PixelFormat.I420, luma: 235), recorder);
        Assert.AreEqual(255, recorder.FirstLuma, 1, "video-range white is full-range 255");
    }

    [TestMethod]
    public void Process_GreyToBgra_GivesEqualChannels()
    {
        using IVideoProcessor processor = Factory.Create(
            I420Input,
            new VideoProcessing(VideoConstraints.Cpu(PixelFormat.Bgra))
        );
        Recorder recorder = new();

        processor.Process(Flat(PixelFormat.I420, luma: 128), recorder);

        // Video-range 128 is (128 - 16) * 255 / 219 in full range.
        Assert.AreEqual(PixelFormat.Bgra, recorder.Format.PixelFormat);
        CollectionAssert.AreEqual(
            new byte[] { 130, 130, 130, 255 },
            recorder.FirstPixel,
            "BGRA of grey"
        );
    }

    [TestMethod]
    public void Retain_Output_StaysValidAfterTheNextFrame()
    {
        using IVideoProcessor processor = Factory.Create(
            I420Input,
            new VideoProcessing(VideoConstraints.Cpu(PixelFormat.Nv12))
        );
        Keeper keeper = new();

        processor.Process(Flat(PixelFormat.I420, luma: 50), keeper);
        processor.Process(Flat(PixelFormat.I420, luma: 200), keeper);

        using VideoFrameLease first = keeper.Leases[0];
        using VideoFrameLease second = keeper.Leases[1];
        Assert.AreEqual(50, first.Frame.GetPlane(0)[0]);
        Assert.AreEqual(200, second.Frame.GetPlane(0)[0]);
    }

    [TestMethod]
    public void QueryCapabilities_GpuInputAlphaOrGpuOutput_IsRefused()
    {
        VideoProcessing toNv12 = new(VideoConstraints.Cpu(PixelFormat.Nv12));

        Assert.IsNull(
            Factory.QueryCapabilities(I420Input with { Storage = VideoStorageKind.D3D11 }, toNv12)
        );
        Assert.IsNull(
            Factory.QueryCapabilities(I420Input, toNv12 with { Alpha = AlphaLayout.PackSideBySide })
        );
        Assert.IsNull(
            Factory.QueryCapabilities(
                I420Input,
                new VideoProcessing(
                    new VideoConstraints([VideoStorageKind.D3D11], [PixelFormat.Nv12])
                )
            )
        );
        Assert.ThrowsExactly<ArgumentException>(() =>
            Factory.Create(I420Input, toNv12 with { Alpha = AlphaLayout.UnpackSideBySide })
        );
    }

    private static VideoFrame Flat(PixelFormat format, byte luma)
    {
        byte[] pixels = new byte[PlaneLayout.PackedSize(format, Pictures.Size)];
        pixels.AsSpan(0, Pictures.Width * Pictures.Height).Fill(luma);
        pixels.AsSpan(Pictures.Width * Pictures.Height).Fill(128);
        return new VideoFrame(
            new CpuImage(PlaneLayout.Packed(format, Pictures.Size)),
            new VideoFormat(format, Pictures.Width, Pictures.Height),
            Pictures.Timestamp(0),
            pixels,
            VideoColor.Bt709
        );
    }

    private sealed class Recorder : IVideoFrameConsumer
    {
        public VideoFormat Format { get; private set; }

        public MediaTimestamp Timestamp { get; private set; }

        public VideoColor Color { get; private set; }

        public double MeanLuma { get; private set; }

        public byte FirstLuma { get; private set; }

        public byte FirstChroma { get; private set; }

        public byte[] FirstPixel { get; private set; } = [];

        public void OnFrame(in VideoFrame frame)
        {
            Format = frame.Format;
            Timestamp = frame.Timestamp;
            Color = frame.Color;
            _ = frame.Storage.TryGetValue(out CpuImage image);
            ReadOnlySpan<byte> first = frame.GetPlane(0);
            FirstLuma = first[0];
            FirstPixel = first[..4].ToArray();
            if (frame.Format.PixelFormat == PixelFormat.Bgra)
            {
                return;
            }

            FirstChroma = frame.GetPlane(1)[0];
            int stride = image.Planes[0].Stride;
            long sum = 0;
            for (int y = 0; y < frame.Format.VisibleRect.Height; y++)
            {
                foreach (byte value in first.Slice(y * stride, frame.Format.VisibleRect.Width))
                {
                    sum += value;
                }
            }

            MeanLuma =
                sum / (double)(frame.Format.VisibleRect.Width * frame.Format.VisibleRect.Height);
        }
    }

    private sealed class Keeper : IVideoFrameConsumer
    {
        public List<VideoFrameLease> Leases { get; } = [];

        public void OnFrame(in VideoFrame frame) => Leases.Add(frame.Retain());
    }
}
