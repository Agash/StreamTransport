using System.Runtime.InteropServices;
using Agash.StreamTransport.MacOS.Audio;
using Agash.StreamTransport.Media;
using AVFoundation;

namespace Agash.StreamTransport.MacOS.Tests;

[TestClass]
public sealed class CapturedAudioTests
{
    [TestMethod]
    public void StereoAt48k_PassesThroughInterleavedWithTheCaptureTime()
    {
        using CapturedAudio conversion = new();
        using AVAudioFormat format = new(48_000, 2);
        using AVAudioPcmBuffer buffer = Buffer(
            format,
            480,
            (channel, _) => channel == 0 ? 0.25f : -0.25f
        );
        ulong hostTime = AVAudioTime.HostTimeForSeconds(123);
        using var when = AVAudioTime.FromHostTime(hostTime);
        Recorder recorder = new();

        Assert.IsTrue(conversion.TryConvert(buffer, when, recorder, out string? failure), failure);

        Assert.AreEqual(CoreAudioSink.Format, recorder.Format);
        Assert.AreEqual(TimestampKind.Capture, recorder.Kinds.Single());
        Assert.AreEqual(
            (long)(AVAudioTime.SecondsForHostTime(hostTime) * 1e9),
            recorder.Times[0].Nanoseconds,
            "host time becomes media time"
        );
        Assert.HasCount(480 * 2, recorder.Samples);
        for (int i = 0; i < recorder.Samples.Count; i += 2)
        {
            Assert.AreEqual(0.25f, recorder.Samples[i], 1e-6f);
            Assert.AreEqual(-0.25f, recorder.Samples[i + 1], 1e-6f);
        }
    }

    [TestMethod]
    public void MonoAt44k1_IsResampledOntoBothChannels()
    {
        using CapturedAudio conversion = new();
        using AVAudioFormat format = new(44_100, 1);
        Recorder recorder = new();
        const int Frames = 4410;
        for (int block = 0; block < 5; block++)
        {
            using AVAudioPcmBuffer buffer = Buffer(
                format,
                Frames,
                (_, frame) =>
                    0.5f * MathF.Sin(2 * MathF.PI * 1000 * ((block * Frames) + frame) / 44_100f)
            );
            using var when = AVAudioTime.FromHostTime(
                AVAudioTime.HostTimeForSeconds(10 + (block * 0.1))
            );
            Assert.IsTrue(
                conversion.TryConvert(buffer, when, recorder, out string? failure),
                failure
            );
        }

        // Half a second in at 44.1 kHz is about half a second out at 48 kHz, less the resampler's delay.
        int frames = recorder.Samples.Count / 2;
        Assert.IsTrue(frames is > 23_000 and <= 24_000, $"{frames} frames out.");
        double sum = 0;
        for (int i = 0; i < recorder.Samples.Count; i += 2)
        {
            Assert.AreEqual(
                recorder.Samples[i],
                recorder.Samples[i + 1],
                "a mono input is heard on both channels"
            );
            if (i >= 4800)
            {
                sum += recorder.Samples[i] * recorder.Samples[i];
            }
        }

        double rms = Math.Sqrt(sum / ((recorder.Samples.Count - 4800) / 2));
        Assert.AreEqual(0.5 / Math.Sqrt(2), rms, 0.02, "the tone survives resampling");
        Assert.IsTrue(
            recorder.Times.Zip(recorder.Times.Skip(1)).All(p => p.Second > p.First),
            "times rise"
        );
    }

    private static unsafe AVAudioPcmBuffer Buffer(
        AVAudioFormat format,
        int frames,
        Func<int, int, float> sample
    )
    {
        AVAudioPcmBuffer buffer = new(format, (uint)frames) { FrameLength = (uint)frames };
        float** channels = (float**)buffer.FloatChannelData;
        for (int channel = 0; channel < format.ChannelCount; channel++)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                channels[channel][frame] = sample(channel, frame);
            }
        }

        return buffer;
    }

    private sealed class Recorder : IAudioFrameConsumer
    {
        public AudioFormat Format { get; private set; }

        public List<float> Samples { get; } = [];

        public List<MediaTime> Times { get; } = [];

        public List<TimestampKind> Kinds { get; } = [];

        public void OnFrame(in AudioFrame frame)
        {
            Format = frame.Format;
            Samples.AddRange(MemoryMarshal.Cast<byte, float>(frame.Samples));
            Times.Add(frame.Timestamp.Time);
            Kinds.Add(frame.Timestamp.Kind);
        }
    }
}
