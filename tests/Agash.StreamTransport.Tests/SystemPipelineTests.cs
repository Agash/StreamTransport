using Agash.StreamTransport.WebRtc.Dtls;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// System-level test of the whole media path with no hardware: a registry-negotiated <see cref="FakeVideoCodec"/>
/// is driven through negotiation -> encode -> packetize -> SRTP -> ICE (loopback) -> depacketize -> decode ->
/// sink. The video orchestration is otherwise only exercised by hardware-gated tests; this covers it
/// deterministically on any machine, and round-trips the frame dimensions to prove content integrity end to end.
/// </summary>
[TestClass]
public sealed class SystemPipelineTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task VideoOrchestration_FakeCodec_NegotiatesEncodesAndDeliversFrames()
    {
        var registry = new MediaCodecRegistry([new FakeVideoCodec()], []);
        var dtls = new DtlsTransportFactory();
        var loggers = NullLoggerFactory.Instance;

        const int width = 320;
        const int height = 240;

        var senderSignaling = new LoopbackSignaling();
        var receiverSignaling = new LoopbackSignaling();
        senderSignaling.Peer = receiverSignaling;
        receiverSignaling.Peer = senderSignaling;

        var sink = new CollectingVideoSink(target: 5);
        await using var receiver = new WebRtcMediaReceiver(new MediaTransportOptions(), registry, dtls, loggers, video: sink);
        await using var sender = new WebRtcMediaSender(new MediaTransportOptions(), registry, dtls, loggers, video: new PatternVideoSource(width, height));

        await receiver.StartAsync(receiverSignaling);
        await sender.StartAsync(senderSignaling);

        Task finished = await Task.WhenAny(sink.Reached, Task.Delay(25_000));

        await sender.StopAsync();
        await receiver.StopAsync();
        await senderSignaling.DisposeAsync();
        await receiverSignaling.DisposeAsync();

        Assert.AreSame(sink.Reached, finished, $"expected >=5 frames through the full pipeline, got {sink.Count}.");
        Assert.AreEqual(width, sink.LastWidth, "frame width must survive the whole encode/transport/decode round trip.");
        Assert.AreEqual(height, sink.LastHeight);
    }
}
