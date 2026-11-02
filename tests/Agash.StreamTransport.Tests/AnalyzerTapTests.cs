using System.Collections.Immutable;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.TestSignal;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// The analyzer's video tap sits in front of a real output: every frame still reaches the output, and
/// a frame the analyzer cannot read in place (RGB, or a GPU texture) is measured through a read-back.
/// </summary>
[TestClass]
public sealed class AnalyzerTapTests
{
    private static readonly VideoSize Size = new(64, 32);

    [TestMethod]
    public void Tap_FrameNotInYuv_ReachesTheOutputAndIsReadBack()
    {
        TestSignalAnalyzer analyzer = new();
        ReadBackFactory factory = new();
        MediaCodecRegistry registry = new([], [], [factory], [], [], null);
        RecordingSink output = new();
        IVideoSink tap = analyzer.WrapVideo(output, registry);

        // A processor hands its output to the tap as a consumer, as the receive path's processors do.
        IVideoFrameConsumer consumer = tap;
        consumer.OnFrame(Frame(PixelFormat.Bgra));

        CollectionAssert.AreEqual(new[] { PixelFormat.Bgra }, output.Received.ToArray());
        Assert.AreEqual(1, factory.Processed, "the BGRA frame was read back for the analyzer");
        Assert.AreEqual(
            1,
            analyzer.Measure().Unreadable,
            "the analyzer saw the read-back (a blank picture has no barcode)"
        );
        ((IDisposable)tap).Dispose();
    }

    [TestMethod]
    public void Tap_FrameInNv12_IsMeasuredInPlace()
    {
        TestSignalAnalyzer analyzer = new();
        ReadBackFactory factory = new();
        MediaCodecRegistry registry = new([], [], [factory], [], [], null);
        RecordingSink output = new();
        IVideoSink tap = analyzer.WrapVideo(output, registry);

        tap.OnFrame(Frame(PixelFormat.Nv12));

        CollectionAssert.AreEqual(new[] { PixelFormat.Nv12 }, output.Received.ToArray());
        Assert.AreEqual(0, factory.Processed, "NV12 in memory needs no read-back");
        Assert.AreEqual(1, analyzer.Measure().Unreadable);
        ((IDisposable)tap).Dispose();
    }

    private static VideoFrame Frame(PixelFormat format) =>
        new(
            new CpuImage(PlaneLayout.Packed(format, Size)),
            new VideoFormat(format, Size.Width, Size.Height),
            MediaTimestamp.Captured(MediaTime.Zero),
            new byte[PlaneLayout.PackedSize(format, Size)],
            VideoColor.Bt709
        );

    private sealed class RecordingSink : IVideoSink
    {
        private readonly List<PixelFormat> _received = [];

        public VideoConstraints Constraints { get; } =
            VideoConstraints.Cpu(PixelFormat.Bgra, PixelFormat.Nv12);

        public ImmutableArray<PixelFormat> Received => [.. _received];

        public void OnFrame(in VideoFrame frame) => _received.Add(frame.Format.PixelFormat);
    }

    // Reads any frame back as a blank NV12 picture in memory.
    private sealed class ReadBackFactory : IVideoProcessorFactory
    {
        public int Processed { get; private set; }

        public int Rank => 0;

        public VideoProcessorInfo? QueryCapabilities(
            VideoStreamDescription input,
            VideoProcessing processing
        ) =>
            new(
                "read-back",
                false,
                input with
                {
                    Storage = VideoStorageKind.Cpu,
                    PixelFormat = PixelFormat.Nv12,
                }
            );

        public IVideoProcessor Create(VideoStreamDescription input, VideoProcessing processing) =>
            new Reader(this, QueryCapabilities(input, processing)!);

        private sealed class Reader(ReadBackFactory factory, VideoProcessorInfo info)
            : IVideoProcessor
        {
            public VideoProcessorInfo Info => info;

            public void Process(in VideoFrame frame, IVideoFrameConsumer consumer)
            {
                factory.Processed++;
                consumer.OnFrame(Frame(PixelFormat.Nv12));
            }

            public void Dispose() { }
        }
    }
}
