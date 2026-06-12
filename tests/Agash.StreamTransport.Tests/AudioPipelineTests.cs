using System.Runtime.InteropServices;
using Agash.StreamTransport.Codecs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// Round-trips PCM through the Opus <see cref="AudioPipeline"/> (Concentus, pure-managed - runs in CI). Guards
/// stereo support and the per-channel RTP duration: an interleaved stereo frame must not advance the clock at
/// twice the rate, and the decoder must emit two channels.
/// </summary>
[TestClass]
public sealed class AudioPipelineTests
{
    private const int SampleRate = 48_000;
    private const int SamplesPerChannel = 960; // 20 ms.

    [TestMethod]
    public void Stereo_EncodeDecode_KeepsTwoChannels_AndPerChannelDuration()
    {
        var pipeline = new AudioPipeline(AudioCodec.Opus);
        AudioFrame frame = StereoSine(SamplesPerChannel);

        (uint duration, byte[] payload) = pipeline.Encode(frame);

        Assert.AreEqual((uint)SamplesPerChannel, duration,
            "RTP duration must be per-channel samples, not the interleaved total (the stereo timestamp bug).");
        Assert.IsTrue(payload.Length > 0, "Opus should produce a payload.");

        AudioFrame decoded = pipeline.Decode(payload, 0);
        Assert.AreEqual(2, decoded.Channels, "the decoder emits stereo.");
        Assert.AreEqual(SampleRate, decoded.SampleRate);
        Assert.IsTrue(decoded.Samples.Length >= SamplesPerChannel * 2 * sizeof(short) - 64,
            $"expected ~{SamplesPerChannel} stereo samples back, got {decoded.Samples.Length / 2 / sizeof(short)}.");
    }

    [TestMethod]
    public void MonoSource_StillEncodes_AndDecodesAsStereo()
    {
        var pipeline = new AudioPipeline(AudioCodec.Opus);
        short[] mono = new short[SamplesPerChannel];
        for (int i = 0; i < mono.Length; i++)
        {
            mono[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / SampleRate) * 8000);
        }

        var frame = new AudioFrame(AsBytes(mono), AudioSampleFormat.S16, SampleRate, 1, 0);

        (uint duration, byte[] payload) = pipeline.Encode(frame);
        Assert.AreEqual((uint)SamplesPerChannel, duration, "a mono frame advances the clock by its sample count.");

        AudioFrame decoded = pipeline.Decode(payload, 0);
        Assert.AreEqual(2, decoded.Channels, "a mono stream still decodes to two channels.");
    }

    [TestMethod]
    public void Stereo_RoundTrip_PreservesAudibleEnergy()
    {
        var pipeline = new AudioPipeline(AudioCodec.Opus);

        // Run several frames so the decode is past Opus' initial pre-skip, then check the signal survived.
        AudioFrame decoded = default;
        for (int i = 0; i < 5; i++)
        {
            (_, byte[] payload) = pipeline.Encode(StereoSine(SamplesPerChannel));
            decoded = pipeline.Decode(payload, 0);
        }

        ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(decoded.Samples.Span);
        long sumSquares = 0;
        foreach (short s in samples)
        {
            sumSquares += (long)s * s;
        }

        double rms = Math.Sqrt(sumSquares / (double)Math.Max(1, samples.Length));
        Assert.IsTrue(rms > 200, $"Decoded audio RMS {rms:F0} too low - signal did not survive the round-trip.");
    }

    private static AudioFrame StereoSine(int perChannel)
    {
        short[] interleaved = new short[perChannel * 2];
        for (int i = 0; i < perChannel; i++)
        {
            interleaved[(i * 2) + 0] = (short)(Math.Sin(2 * Math.PI * 440 * i / SampleRate) * 8000); // L
            interleaved[(i * 2) + 1] = (short)(Math.Sin(2 * Math.PI * 880 * i / SampleRate) * 8000); // R
        }

        return new AudioFrame(AsBytes(interleaved), AudioSampleFormat.S16, SampleRate, 2, 0);
    }

    private static byte[] AsBytes(short[] samples) => MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
}
