using Agash.StreamTransport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

[TestClass]
public sealed class AudioLoopbackTests
{
    [TestMethod]
    [Timeout(40_000)]
    public async Task AudioLoopback_SenderToReceiver_DeliversDecodedFrames()
    {
        var senderSignaling = new LoopbackSignaling();
        var receiverSignaling = new LoopbackSignaling();
        senderSignaling.Peer = receiverSignaling;
        receiverSignaling.Peer = senderSignaling;

        var sink = new CollectingAudioSink(target: 10);
        await using var receiver = new WebRtcMediaReceiver(new MediaTransportOptions(), TestMedia.Codecs, TestMedia.Dtls, TestMedia.Loggers, audio: sink);
        await using var sender = new WebRtcMediaSender(new MediaTransportOptions(), TestMedia.Codecs, TestMedia.Dtls, TestMedia.Loggers, audio: new ToneAudioSource());

        // Receiver subscribes first so it is ready to answer the sender's offer.
        await receiver.StartAsync(receiverSignaling);
        await sender.StartAsync(senderSignaling);

        Task finished = await Task.WhenAny(sink.Reached, Task.Delay(35_000));

        await sender.StopAsync();
        await receiver.StopAsync();
        await senderSignaling.DisposeAsync();
        await receiverSignaling.DisposeAsync();

        Assert.IsTrue(
            ReferenceEquals(finished, sink.Reached) && sink.Count >= 10,
            $"Expected at least 10 decoded audio frames at the sink, got {sink.Count}.");
    }
}
