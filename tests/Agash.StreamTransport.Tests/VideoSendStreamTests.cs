using System.Collections.Immutable;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.Rtp;
using Agash.StreamTransport.Streams;
using Agash.StreamTransport.Sync;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.Tests;

[TestClass]
public sealed class VideoSendStreamTests
{
    private static readonly VideoCodecFormat H264 = new(VideoCodecId.H264);

    [TestMethod]
    public async Task EncoderFailingBeforeItsFirstFrame_IsReplacedByTheNextBest()
    {
        Encoders broken = new(rank: 9, "broken", failsOnEncode: true);
        Encoders working = new(rank: 1, "working");
        MediaCodecRegistry registry = new([broken, working], [], [], [], []);
        TaskCompletionSource sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using RtpPacer pacer = new(
            (_, _) =>
            {
                sent.TrySetResult();
                return ValueTask.CompletedTask;
            },
            10_000_000,
            TimeProvider.System
        );

        await using VideoSendStream stream = new(
            new GradientSource(TimeSpan.FromMilliseconds(10)),
            new VideoSendSetup(
                H264,
                new RtpStreamWriter(
                    H264PayloadFormat.Instance.CreatePacketizer(1200),
                    96,
                    1,
                    new ClockRate(90_000),
                    new CaptureClock(MediaClock.System)
                ),
                1_000_000
            ),
            registry,
            new MediaSessionOptions(),
            pacer,
            MediaClock.System,
            new StreamTransportMetrics(meterFactory: null),
            NullLogger.Instance
        );
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);

        Assert.AreEqual(1, broken.Created, "the broken encoder is not tried again");
        Assert.IsGreaterThan(0, stream.FramesSent);
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed class Encoders(int rank, string name, bool failsOnEncode = false)
        : IVideoEncoderFactory
    {
        private readonly VideoEncoderInfo _info = new(
            name,
            false,
            VideoConstraints.Cpu(PixelFormat.I420),
            2,
            2,
            new VideoSize(4096, 4096),
            true
        );

        public int Created { get; private set; }

        public int Rank => rank;

        public ImmutableArray<VideoCodecFormat> SupportedFormats => [H264];

        public VideoEncoderInfo? QueryCapabilities(VideoCodecFormat format, GpuIdentity? device) =>
            format.Codec == H264.Codec ? _info : null;

        public IVideoEncoder Create(VideoEncoderConfiguration configuration, GpuIdentity? device)
        {
            Created++;
            return new Encoder(_info, failsOnEncode);
        }
    }

    // Emits one IDR NAL per frame, or refuses the first frame the way a driver refuses at open.
    private sealed class Encoder(VideoEncoderInfo info, bool failsOnEncode) : IVideoEncoder
    {
        private static readonly byte[] Idr = [0, 0, 0, 1, 0x65, 0x88, 0x84, 0x00];

        public VideoEncoderInfo Info => info;

        public void Encode(
            in VideoFrame frame,
            in EncodeRequest request,
            IEncodedVideoConsumer consumer
        )
        {
            if (failsOnEncode)
            {
                throw new NotSupportedException("No supported profiles for given format.");
            }

            consumer.OnEncoded(
                new EncodedVideoFrame(Idr, VideoCodecId.H264, true, frame.Timestamp)
            );
        }

        public void Reconfigure(in RateTarget target) { }

        public void Flush(IEncodedVideoConsumer consumer) { }

        public void Dispose() { }
    }
}
