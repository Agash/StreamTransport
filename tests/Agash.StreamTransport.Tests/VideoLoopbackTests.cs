using Agash.StreamTransport;
using Agash.StreamTransport.Codecs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

// Hardware encode + real loopback sockets + full WebRTC handshake: the heaviest test; isolate it from the
// parallel pool so neither the encoder session nor the handshake is starved.
[TestClass]
[DoNotParallelize]
public sealed class VideoLoopbackTests
{
    /// <summary>Auto-selected encoder (NVENC on Windows/Linux, VideoToolbox on macOS): source -> encode -> WebRTC -> decode -> sink.</summary>
    [TestMethod]
    [Timeout(60_000)]
    public Task VideoLoopback_AutoEncoder_DeliversDecodedFrames() => RunLoopbackAsync(encoderName: null);

    /// <summary>Forces the AMD AMF encoder end to end, so the full transport is exercised with AMF too.</summary>
    [TestMethod]
    [Timeout(60_000)]
    public Task VideoLoopback_Amf_DeliversDecodedFrames() => RunLoopbackAsync("hevc_amf");

    /// <summary>Forces the Intel QSV encoder end to end (skips where no Intel GPU is present).</summary>
    [TestMethod]
    [Timeout(60_000)]
    public Task VideoLoopback_Qsv_DeliversDecodedFrames() => RunLoopbackAsync("hevc_qsv");

    /// <summary>Forces Apple VideoToolbox end to end (macOS; self-skips elsewhere).</summary>
    [TestMethod]
    [Timeout(60_000)]
    public Task VideoLoopback_VideoToolbox_DeliversDecodedFrames() => RunLoopbackAsync("hevc_videotoolbox");

    /// <summary>Forces Linux VAAPI end to end (self-skips elsewhere).</summary>
    [TestMethod]
    [Timeout(60_000)]
    public Task VideoLoopback_Vaapi_DeliversDecodedFrames() => RunLoopbackAsync("hevc_vaapi");

    private static async Task RunLoopbackAsync(string? encoderName)
    {
        string? nativeBin = TestNative.FindFFmpegBin();
        if (nativeBin is null)
        {
            Assert.Inconclusive("No bundled FFmpeg native build found; skipping video loopback test.");
            return;
        }

        FFmpegLibrary.EnsureLoaded(nativeBin);

        // For the auto case, probe the same encoder the sender will pick (VideoToolbox/rkmpp/nvenc/qsv/amf);
        // for a forced encoder, just check it is in the build. Skip if unavailable.
        string probe;
        if (encoderName is null)
        {
            try
            {
                probe = HevcEncoderSelector.Select();
            }
            catch (NotSupportedException ex)
            {
                Assert.Inconclusive($"No hardware HEVC encoder available on this machine: {ex.Message}");
                return;
            }
        }
        else
        {
            probe = encoderName;
            if (!FFmpegLibrary.HasEncoder(probe))
            {
                Assert.Inconclusive($"{probe} is not present in this FFmpeg build.");
                return;
            }
        }

        const int width = 1280;
        const int height = 720;

        // Pre-flight the encoder: it is opened lazily on the send pump, so a missing-GPU failure would
        // otherwise hang rather than report. Opening it here surfaces availability up front.
        try
        {
            using var preflight = TestEncoders.Open(probe, width, height, fps: 30, bitrate: 4_000_000);
        }
        catch (HardwareEncoderUnavailableException ex)
        {
            Assert.Inconclusive($"{probe} hardware is not available on this machine: {ex.Message}");
            return;
        }

        var options = new MediaTransportOptions { VideoEncoderName = encoderName };

        var senderSignaling = new LoopbackSignaling();
        var receiverSignaling = new LoopbackSignaling();
        senderSignaling.Peer = receiverSignaling;
        receiverSignaling.Peer = senderSignaling;

        var sink = new CollectingVideoSink(target: 5);
        await using var receiver = new WebRtcMediaReceiver(new MediaTransportOptions(), TestMedia.Codecs, TestMedia.Dtls, TestMedia.Loggers, video: sink);
        await using var sender = new WebRtcMediaSender(options, TestMedia.Codecs, TestMedia.Dtls, TestMedia.Loggers, video: new PatternVideoSource(width, height));

        await receiver.StartAsync(receiverSignaling);

        try
        {
            await sender.StartAsync(senderSignaling);
        }
        catch (NotSupportedException ex)
        {
            Assert.Inconclusive($"{probe} hardware is not available on this machine: {ex.Message}");
            return;
        }

        Task finished = await Task.WhenAny(sink.Reached, Task.Delay(55_000));

        await sender.StopAsync();
        await receiver.StopAsync();
        await senderSignaling.DisposeAsync();
        await receiverSignaling.DisposeAsync();

        Assert.IsTrue(
            ReferenceEquals(finished, sink.Reached) && sink.Count >= 5,
            $"Expected at least 5 decoded video frames at the sink, got {sink.Count}.");
        Assert.AreEqual(width, sink.LastWidth, "Decoded frame width should match.");
        Assert.AreEqual(height, sink.LastHeight, "Decoded frame height should match.");
    }
}
