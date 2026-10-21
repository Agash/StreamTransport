using System.Collections.Immutable;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// A route follows its sink's constraints frame by frame: when the sink comes to accept another format
/// (its consumer picked one of several it offered), frames are converted into that from then on.
/// </summary>
[TestClass]
public sealed class VideoRouteTests
{
    private static readonly VideoSize Size = new(16, 8);

    [TestMethod]
    public void Route_SinkConstraintsChange_ConvertsIntoTheNewFormat()
    {
        RecordingFactory factory = new();
        MediaCodecRegistry registry = new([], [], [factory], [], [], null);
        SwitchingSink sink = new();
        ManualSource source = new();
        using IDisposable route = registry.Route(source, sink);

        source.Send(PixelFormat.I420);
        sink.Accept(PixelFormat.Bgra);
        source.Send(PixelFormat.I420);
        sink.Accept(PixelFormat.Rgba);
        source.Send(PixelFormat.I420);
        sink.Accept(PixelFormat.I420);
        source.Send(PixelFormat.I420);

        CollectionAssert.AreEqual(
            new[] { PixelFormat.I420, PixelFormat.Bgra, PixelFormat.Rgba, PixelFormat.I420 },
            sink.Received.ToArray()
        );
        Assert.AreEqual(2, factory.Created, "a processor for each format the sink came to take");
    }

    private static VideoFrame Frame(PixelFormat format, byte[] pixels) =>
        new(
            new CpuImage(PlaneLayout.Packed(format, Size)),
            new VideoFormat(format, Size.Width, Size.Height),
            MediaTimestamp.Captured(MediaTime.Zero),
            pixels,
            VideoColor.Bt709
        );

    private sealed class ManualSource : IVideoSource
    {
        private IVideoFrameConsumer? _consumer;

        public IDisposable Connect(IVideoFrameConsumer consumer, VideoConstraints constraints)
        {
            _consumer = consumer;
            return new Disconnect(this);
        }

        public void Send(PixelFormat format)
        {
            byte[] pixels = new byte[PlaneLayout.PackedSize(format, Size)];
            _consumer!.OnFrame(Frame(format, pixels));
        }

        private sealed class Disconnect(ManualSource source) : IDisposable
        {
            public void Dispose() => source._consumer = null;
        }
    }

    // Accepts one format at a time, handing out a new constraints instance when it changes.
    private sealed class SwitchingSink : IVideoSink
    {
        private readonly List<PixelFormat> _received = [];

        public VideoConstraints Constraints { get; private set; } =
            VideoConstraints.Cpu(PixelFormat.I420);

        public ImmutableArray<PixelFormat> Received => [.. _received];

        public void Accept(PixelFormat format) => Constraints = VideoConstraints.Cpu(format);

        public void OnFrame(in VideoFrame frame) => _received.Add(frame.Format.PixelFormat);
    }

    // Converts into the first format asked for, in memory.
    private sealed class RecordingFactory : IVideoProcessorFactory
    {
        public int Created { get; private set; }

        public int Rank => 0;

        public VideoProcessorInfo? QueryCapabilities(
            VideoStreamDescription input,
            VideoProcessing processing
        ) =>
            new(
                "recording",
                false,
                input with
                {
                    Storage = VideoStorageKind.Cpu,
                    PixelFormat = processing.Output.PixelFormats[0],
                }
            );

        public IVideoProcessor Create(VideoStreamDescription input, VideoProcessing processing)
        {
            Created++;
            return new Converter(QueryCapabilities(input, processing)!);
        }

        private sealed class Converter(VideoProcessorInfo info) : IVideoProcessor
        {
            public VideoProcessorInfo Info => info;

            public void Process(in VideoFrame frame, IVideoFrameConsumer consumer)
            {
                PixelFormat format = info.Output.PixelFormat;
                consumer.OnFrame(Frame(format, new byte[PlaneLayout.PackedSize(format, Size)]));
            }

            public void Dispose() { }
        }
    }
}
