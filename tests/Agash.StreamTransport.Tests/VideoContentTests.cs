using Agash.StreamTransport;
using Agash.StreamTransport.Codecs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// End-to-end content verification: a producer emits a known luma structure (bright top / dark bottom), and
/// after encode → WebRTC → decode the decoded frame must still show that structure. Unlike
/// <see cref="VideoLoopbackTests"/> (which checks frame count + dimensions), this asserts the actual decoded
/// pixels, so it catches black-output, plane-swap, and colour/format wiring bugs that survive a frame-count
/// check (the M-1 / W-4 class). Parametrized per encoder; self-skips where the encoder/GPU is absent.
/// </summary>
[TestClass]
public sealed class VideoContentTests
{
    /// <summary>Auto-selected encoder (VideoToolbox on macOS, NVENC on Win/Linux, ...).</summary>
    [TestMethod]
    [Timeout(60_000)]
    public Task VideoContent_AutoEncoder_PreservesLumaStructure() => RunAsync(null);

    /// <summary>NVIDIA NVENC explicitly (Windows/Linux).</summary>
    [TestMethod]
    [Timeout(60_000)]
    public Task VideoContent_Nvenc_PreservesLumaStructure() => RunAsync("hevc_nvenc");

    /// <summary>AMD AMF explicitly (Windows).</summary>
    [TestMethod]
    [Timeout(60_000)]
    public Task VideoContent_Amf_PreservesLumaStructure() => RunAsync("hevc_amf");

    /// <summary>Intel QSV explicitly.</summary>
    [TestMethod]
    [Timeout(60_000)]
    public Task VideoContent_Qsv_PreservesLumaStructure() => RunAsync("hevc_qsv");

    /// <summary>Apple VideoToolbox explicitly (macOS; self-skips elsewhere).</summary>
    [TestMethod]
    [Timeout(60_000)]
    public Task VideoContent_VideoToolbox_PreservesLumaStructure() => RunAsync("hevc_videotoolbox");

    /// <summary>Linux VAAPI explicitly (Intel/AMD on Linux; self-skips elsewhere).</summary>
    [TestMethod]
    [Timeout(60_000)]
    public Task VideoContent_Vaapi_PreservesLumaStructure() => RunAsync("hevc_vaapi");

    private static async Task RunAsync(string? encoderName)
    {
        string? nativeBin = TestNative.FindFFmpegBin();
        if (nativeBin is null)
        {
            Assert.Inconclusive("No bundled FFmpeg native build found.");
            return;
        }

        FFmpegLibrary.EnsureLoaded(nativeBin);

        string probe;
        if (encoderName is null)
        {
            try
            {
                probe = HevcEncoderSelector.Select();
            }
            catch (NotSupportedException ex)
            {
                Assert.Inconclusive($"No hardware HEVC encoder available: {ex.Message}");
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

        const int width = 640;
        const int height = 480;

        try
        {
            using var preflight = new HardwareHevcEncoder(probe, width, height, fps: 30, bitrate: 4_000_000);
        }
        catch (HardwareEncoderUnavailableException ex)
        {
            Assert.Inconclusive($"{probe} hardware is not available: {ex.Message}");
            return;
        }

        var options = new MediaTransportOptions { VideoEncoderName = encoderName };

        var senderSignaling = new LoopbackSignaling();
        var receiverSignaling = new LoopbackSignaling();
        senderSignaling.Peer = receiverSignaling;
        receiverSignaling.Peer = senderSignaling;

        var sink = new CollectingVideoSink(target: 5);
        await using var receiver = new WebRtcMediaReceiver(new MediaTransportOptions(), TestMedia.Codecs, TestMedia.Dtls, TestMedia.Loggers, video: sink);
        await using var sender = new WebRtcMediaSender(options, TestMedia.Codecs, TestMedia.Dtls, TestMedia.Loggers, video: new StructuredVideoSource(width, height));

        await receiver.StartAsync(receiverSignaling);
        try
        {
            await sender.StartAsync(senderSignaling);
        }
        catch (NotSupportedException ex)
        {
            Assert.Inconclusive($"{probe} hardware is not available: {ex.Message}");
            return;
        }

        Task finished = await Task.WhenAny(sink.Reached, Task.Delay(55_000));

        await sender.StopAsync();
        await receiver.StopAsync();
        await senderSignaling.DisposeAsync();
        await receiverSignaling.DisposeAsync();

        Assert.IsTrue(ReferenceEquals(finished, sink.Reached) && sink.Count >= 5,
            $"Expected at least 5 decoded frames, got {sink.Count}.");
        Assert.AreEqual(width, sink.LastWidth);
        Assert.AreEqual(height, sink.LastHeight);

        byte[]? pixels = sink.SnapshotPixels();
        Assert.IsNotNull(pixels, "decoded frame should carry CPU pixels for the content check.");
        Assert.IsTrue(sink.LastFormat is VideoPixelFormat.Nv12 or VideoPixelFormat.I420,
            $"expected a YUV decode for the luma check, got {sink.LastFormat}.");

        // Y plane is the first width*height bytes (row stride = width) for both NV12 and I420. Sample well
        // inside the bright (top) and dark (bottom) halves, away from the boundary the codec smooths.
        int brightY = pixels[(height / 4 * width) + (width / 2)];
        int darkY = pixels[(3 * height / 4 * width) + (width / 2)];

        Assert.IsTrue(brightY > 150, $"bright region luma {brightY} too low - content lost or black (expected ~{StructuredVideoSource.BrightY}).");
        Assert.IsTrue(darkY < 90, $"dark region luma {darkY} too high (expected ~{StructuredVideoSource.DarkY}).");
        Assert.IsTrue(brightY - darkY > 60, $"luma structure flattened (bright {brightY} vs dark {darkY}) - a plane/format bug.");
    }
}
