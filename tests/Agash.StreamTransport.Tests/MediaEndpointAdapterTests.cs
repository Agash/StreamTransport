using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// The output adapters a provider wraps its sink in hand on everything the sink states, the members
/// with defaults included: a session times playout from the output latency, and draws into the
/// surface the sink lends.
/// </summary>
[TestClass]
public sealed class MediaEndpointAdapterTests
{
    [TestMethod]
    public void VideoOutput_ForwardsLatencyConstraintsAndRender()
    {
        LendingSink sink = new();
        using VideoOutput output = new(sink, "test", "lending");

        Assert.AreEqual(TimeSpan.FromMilliseconds(17), output.OutputLatency);
        Assert.AreSame(sink.Constraints, output.Constraints);
        VideoRenderResult result = output.Render(
            new VideoFormat(PixelFormat.Bgra, 4, 4),
            0,
            static (in VideoTarget _, scoped in int _) => true
        );
        Assert.AreEqual(VideoRenderResult.Rendered, result);
        Assert.AreEqual(1, sink.Rendered);
    }

    [TestMethod]
    public void AudioOutput_ForwardsLatencyAndConstraints()
    {
        LatentAudioSink sink = new();
        using AudioOutput output = new(sink, "test", "latent");

        Assert.AreEqual(TimeSpan.FromMilliseconds(23), output.OutputLatency);
        Assert.AreSame(sink.Constraints, output.Constraints);
    }

    private sealed class LendingSink : IVideoSink
    {
        public int Rendered { get; private set; }

        public TimeSpan? OutputLatency => TimeSpan.FromMilliseconds(17);

        public VideoConstraints Constraints { get; } = VideoConstraints.Cpu(PixelFormat.Bgra);

        public void OnFrame(in VideoFrame frame) { }

        public VideoRenderResult Render<TState>(
            VideoFormat format,
            scoped in TState state,
            VideoTargetRenderer<TState> render
        )
            where TState : allows ref struct
        {
            Rendered++;
            return VideoRenderResult.Rendered;
        }
    }

    private sealed class LatentAudioSink : IAudioSink
    {
        public TimeSpan? OutputLatency => TimeSpan.FromMilliseconds(23);

        public AudioConstraints Constraints { get; } = new([48_000], [SampleFormat.F32], 2);

        public void OnFrame(in AudioFrame frame) { }
    }
}
