using System.Collections.Immutable;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Tests;

[TestClass]
public sealed class MediaCodecRegistryTests
{
    private static readonly VideoCodecFormat Vp9 = new(new VideoCodecId("VP9"));

    [TestMethod]
    public void TryCreateVideoEncoder_PicksTheHighestRankThatCanDoIt()
    {
        FakeEncoders low = new(rank: 1, "low");
        FakeEncoders high = new(rank: 9, "high");
        FakeEncoders cannot = new(rank: 20, "cannot", supports: false);
        MediaCodecRegistry registry = Registry(encoders: [low, cannot, high]);

        Assert.IsTrue(
            registry.TryCreateVideoEncoder(Configuration(), null, out IVideoEncoder? encoder)
        );
        Assert.AreEqual("high", encoder.Info.ImplementationName);
        Assert.AreEqual("high", registry.QueryVideoEncoder(Vp9, null)!.ImplementationName);
        Assert.IsTrue(registry.CanEncode(Vp9.Codec));
        Assert.IsFalse(registry.CanDecode(Vp9.Codec));
    }

    [TestMethod]
    public void TryCreateVideoEncoder_FallsBackWhenTheBestFailsToOpen()
    {
        FakeEncoders broken = new(rank: 9, "broken", throws: true);
        FakeEncoders working = new(rank: 1, "working");
        MediaCodecRegistry registry = Registry(encoders: [broken, working]);

        Assert.IsTrue(
            registry.TryCreateVideoEncoder(Configuration(), null, out IVideoEncoder? encoder)
        );
        Assert.AreEqual("working", encoder.Info.ImplementationName);
        Assert.AreEqual(1, broken.Attempts);
    }

    [TestMethod]
    public void TryCreateVideoEncoder_NothingCanDoIt_ReturnsFalse()
    {
        MediaCodecRegistry registry = Registry(
            encoders: [new FakeEncoders(1, "no", supports: false)]
        );

        Assert.IsFalse(
            registry.TryCreateVideoEncoder(Configuration(), null, out IVideoEncoder? encoder)
        );
        Assert.IsNull(encoder);
    }

    [TestMethod]
    public void Options_Profiles_ExpandToTheirSettings()
    {
        var irl = MediaSessionOptions.For(MediaProfile.IrlContribution);
        Assert.IsTrue(irl.EnableFec);
        Assert.AreEqual(PlayoutMode.Synced, irl.Playout);
        Assert.AreEqual(EncodeTuning.LossResilient, irl.VideoTuning);
        Assert.AreEqual(VideoCodecId.H265, irl.VideoCodecs[0]);

        Assert.AreEqual(
            AlphaLayout.PackSideBySide,
            MediaSessionOptions.For(MediaProfile.AvatarTransparent).Alpha
        );
        Assert.AreEqual(
            EncodeTuning.ScreenContent,
            MediaSessionOptions.For(MediaProfile.ScreenShare).VideoTuning
        );
        Assert.AreEqual(PlayoutMode.OnArrival, new MediaSessionOptions().Playout);
    }

    private static VideoEncoderConfiguration Configuration() =>
        new(Vp9, new VideoSize(64, 64), new RateTarget(1_000_000, 30));

    private static MediaCodecRegistry Registry(IEnumerable<IVideoEncoderFactory> encoders) =>
        new(encoders, [], [], [], []);

    private sealed class FakeEncoders(
        int rank,
        string name,
        bool supports = true,
        bool throws = false
    ) : IVideoEncoderFactory
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

        public int Attempts { get; private set; }

        public int Rank => rank;

        public ImmutableArray<VideoCodecFormat> SupportedFormats => supports ? [Vp9] : [];

        public VideoEncoderInfo? QueryCapabilities(VideoCodecFormat format, GpuIdentity? device) =>
            supports && format.Codec == Vp9.Codec ? _info : null;

        public IVideoEncoder Create(VideoEncoderConfiguration configuration, GpuIdentity? device)
        {
            Attempts++;
            return throws
                ? throw new InvalidOperationException("The driver refused.")
                : new Encoder(_info);
        }
    }

    private sealed class Encoder(VideoEncoderInfo info) : IVideoEncoder
    {
        public VideoEncoderInfo Info => info;

        public void Encode(
            in VideoFrame frame,
            in EncodeRequest request,
            IEncodedVideoConsumer consumer
        ) { }

        public void Reconfigure(in RateTarget target) { }

        public void Flush(IEncodedVideoConsumer consumer) { }

        public void Dispose() { }
    }
}
