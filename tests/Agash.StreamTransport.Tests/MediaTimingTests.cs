using System.Runtime.InteropServices;
using Agash.StreamTransport;
using Agash.StreamTransport.Codecs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// Audio/video timing and synchronisation. The RTP clock half is deterministic and managed (CI everywhere):
/// the Opus audio clock must advance at exactly 48 kHz over many frames regardless of channel count, so a
/// stereo frame can't double the timestamp rate (the A-1 desync bug). The combined-stream half runs the real
/// transport with audio + video together and asserts both deliver in their natural proportion (self-skips
/// without a hardware encoder).
/// </summary>
[TestClass]
[DoNotParallelize] // Drives a hardware HEVC encoder (incl. VAAPI); one GPU session at a time.
public sealed class MediaTimingTests
{
    private const int SampleRate = 48_000;
    private const int SamplesPerChannel = 960; // 20 ms @ 48 kHz.

    [TestMethod]
    public void AudioClock_MonoFrames_AdvanceAt48kHz_NoDrift() => AssertNoClockDrift(channels: 1);

    [TestMethod]
    public void AudioClock_StereoFrames_AdvanceAt48kHz_NoDrift() => AssertNoClockDrift(channels: 2);

    /// <summary>
    /// One wall-clock second of 20 ms frames must accumulate exactly 48 000 RTP units (50 × 960), whether the
    /// source is mono or stereo. A per-interleaved-sample duration would make stereo advance at 96 kHz.
    /// </summary>
    private static void AssertNoClockDrift(int channels)
    {
        var pipeline = new AudioPipeline(AudioCodec.Opus);
        const int frames = 50; // 1 second of 20 ms frames.
        long total = 0;
        for (int i = 0; i < frames; i++)
        {
            (uint duration, _) = pipeline.Encode(Sine(SamplesPerChannel, channels, i));
            total += duration;
        }

        Assert.AreEqual(frames * SamplesPerChannel, total,
            $"{channels}-channel audio must advance the RTP clock at 48 kHz (1 s = 48000 units), not drift.");
    }

    /// <summary>
    /// Audio (~50 fps / 20 ms) and video (~30 fps) sent together over the real transport must both keep
    /// flowing - neither stream stalls while the other runs. (The precise frame ratio is startup-sensitive -
    /// video has encoder/keyframe latency Opus does not - so exact-rate correctness is covered by the
    /// deterministic <c>AudioClock_*</c> tests above; here we assert both streams deliver and audio, which
    /// has no startup latency and a higher cadence, is not outpaced by video.)
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task AudioVideo_Combined_BothStreamsKeepFlowing()
    {
        string? nativeBin = TestNative.FindFFmpegBin();
        if (nativeBin is null)
        {
            Assert.Inconclusive("No bundled FFmpeg native build found.");
            return;
        }

        FFmpegLibrary.EnsureLoaded(nativeBin);

        string probe;
        try
        {
            probe = HevcEncoderSelector.Select();
        }
        catch (NotSupportedException ex)
        {
            Assert.Inconclusive($"No hardware HEVC encoder available: {ex.Message}");
            return;
        }

        const int width = 640;
        const int height = 480;
        try
        {
            using var preflight = TestEncoders.Open(probe, width, height, fps: 30, bitrate: 4_000_000);
            // Opening isn't enough: VideoToolbox opens even when its HW is unavailable/contended (virtualized CI
            // Mac) and only fails at the first encode (-542398533). Encode-probe here so the test reports
            // Inconclusive rather than failing mid-pipeline. Real-hardware verification happens on our machines.
            _ = TestEncoders.EncodeNv12(preflight, HardwareEncoderTestSupport.Nv12Pattern(width, height), width, height);
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"{probe} hardware encode is not available: {ex.Message}");
            return;
        }

        var senderSignaling = new LoopbackSignaling();
        var receiverSignaling = new LoopbackSignaling();
        senderSignaling.Peer = receiverSignaling;
        receiverSignaling.Peer = senderSignaling;

        var videoSink = new CollectingVideoSink(target: 15);   // ~0.5 s of video.
        var audioSink = new CollectingAudioSink(target: 25);    // ~0.5 s of audio.
        await using var receiver = new WebRtcMediaReceiver(new MediaTransportOptions(), TestMedia.Codecs, TestMedia.Dtls, TestMedia.Loggers, video: videoSink, audio: audioSink);
        await using var sender = new WebRtcMediaSender(
            new MediaTransportOptions(), TestMedia.Codecs, TestMedia.Dtls, TestMedia.Loggers, video: new StructuredVideoSource(width, height), audio: new ToneAudioSource());

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

        // Wait until both streams reach their targets (or time out).
        var both = Task.WhenAll(videoSink.Reached, audioSink.Reached);
        Task finished = await Task.WhenAny(both, Task.Delay(45_000));

        await sender.StopAsync();
        await receiver.StopAsync();
        await senderSignaling.DisposeAsync();
        await receiverSignaling.DisposeAsync();

        Assert.AreSame(both, finished, $"both streams should deliver (video {videoSink.Count}, audio {audioSink.Count}).");
        Assert.IsTrue(videoSink.Count >= 15 && audioSink.Count >= 25,
            $"expected both streams flowing; video {videoSink.Count}, audio {audioSink.Count}.");

        // Audio (20 ms cadence, no startup latency) should never be outpaced by video (~33 ms + encoder
        // warm-up). If video led, a stream would be mis-paced.
        Assert.IsTrue(audioSink.Count >= videoSink.Count,
            $"audio ({audioSink.Count}) should not be outpaced by video ({videoSink.Count}).");
    }

    private static AudioFrame Sine(int perChannel, int channels, int frameIndex)
    {
        short[] pcm = new short[perChannel * channels];
        for (int i = 0; i < perChannel; i++)
        {
            short s = (short)(Math.Sin(2 * Math.PI * 440 * (i + (frameIndex * perChannel)) / SampleRate) * 8000);
            for (int c = 0; c < channels; c++)
            {
                pcm[(i * channels) + c] = s;
            }
        }

        return new AudioFrame(MemoryMarshal.AsBytes(pcm.AsSpan()).ToArray(), AudioSampleFormat.S16, SampleRate, channels, 0);
    }
}
