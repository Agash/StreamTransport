using System.Buffers.Binary;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Media.Tests;

[TestClass]
public sealed class AudioConverterTests
{
    [TestMethod]
    public void Convert_S16StereoToF32Mono_MixesToTheMean()
    {
        AudioConverter converter = new(
            new AudioFormat(SampleFormat.S16, 48_000, 2),
            new AudioFormat(SampleFormat.F32, 48_000, 1)
        );
        byte[] input = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(input, 16384);
        BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(2), 0);
        BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(4), -32768);
        BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(6), -32768);
        Collector output = new();

        converter.Convert(Frame(input, converter.Input, 0), output);

        CollectionAssert.AreEqual(new[] { 0.25f, -1f }, output.Samples);
        Assert.AreEqual(TimeSpan.Zero, converter.Delay);
    }

    [TestMethod]
    public void Convert_F32MonoToS16Stereo_CopiesAndClamps()
    {
        AudioConverter converter = new(
            new AudioFormat(SampleFormat.F32, 48_000, 1),
            new AudioFormat(SampleFormat.S16, 48_000, 2)
        );
        Collector output = new();

        converter.Convert(Frame(Floats(0.5f, 2f), converter.Input, 0), output);

        CollectionAssert.AreEqual(
            new[] { 0.5f, 0.5f, 32767 / 32768f, 32767 / 32768f },
            output.Samples
        );
    }

    // Every output sample matches the tone at its own timestamp, so the rate and the stamps are both right.
    [TestMethod]
    [DataRow(44_100, 48_000)]
    [DataRow(48_000, 44_100)]
    [DataRow(16_000, 48_000)]
    public void Convert_AToneAcrossRates_FollowsTheToneAtEachOutputTime(int from, int to)
    {
        const double Hz = 1000;
        AudioConverter converter = new(
            new AudioFormat(SampleFormat.F32, from, 1),
            new AudioFormat(SampleFormat.F32, to, 1)
        );
        Collector output = new();

        // 20 ms frames for a second.
        int chunk = from / 50;
        for (int n = 0; n < 50; n++)
        {
            float[] tone = new float[chunk];
            for (int i = 0; i < chunk; i++)
            {
                tone[i] = (float)Math.Sin(2 * Math.PI * Hz * ((n * chunk) + i) / from);
            }

            converter.Convert(
                Frame(Floats(tone), converter.Input, (double)n * chunk / from),
                output
            );
        }

        Assert.IsGreaterThan(to * 9 / 10, output.Samples.Count);
        double worst = 0;
        for (int j = 64; j < output.Samples.Count; j++)
        {
            double expected = Math.Sin(2 * Math.PI * Hz * output.TimeOf(j, to));
            worst = Math.Max(worst, Math.Abs(output.Samples[j] - expected));
        }

        Assert.IsLessThan(2e-3, worst, $"worst error {worst}");
    }

    // A tone above the output's Nyquist frequency is filtered out, not folded back into the band.
    [TestMethod]
    public void Convert_DownsamplingAToneAboveTheNewNyquist_RejectsIt()
    {
        AudioConverter converter = new(
            new AudioFormat(SampleFormat.F32, 48_000, 1),
            new AudioFormat(SampleFormat.F32, 16_000, 1)
        );
        float[] tone = new float[48_000];
        for (int i = 0; i < tone.Length; i++)
        {
            tone[i] = (float)Math.Sin(2 * Math.PI * 10_000 * i / 48_000.0);
        }

        Collector output = new();
        converter.Convert(Frame(Floats(tone), converter.Input, 0), output);

        double energy = output.Samples.Skip(64).Sum(s => (double)s * s);
        double rms = Math.Sqrt(energy / (output.Samples.Count - 64));
        Assert.IsLessThan(0.01, rms, $"{rms} RMS of a 10 kHz tone survived at 16 kHz");
    }

    // Frame boundaries do not show: small frames produce what one large frame does.
    [TestMethod]
    public void Convert_ChunkedInput_MatchesOneFrame()
    {
        var format = new AudioFormat(SampleFormat.F32, 44_100, 2);
        var target = new AudioFormat(SampleFormat.F32, 48_000, 2);
        float[] noise = new float[44_100 * 2];
        var random = new Random(7);
        for (int i = 0; i < noise.Length; i++)
        {
            noise[i] = (float)((random.NextDouble() * 2) - 1);
        }

        Collector whole = new();
        new AudioConverter(format, target).Convert(Frame(Floats(noise), format, 0), whole);

        Collector chunked = new();
        AudioConverter converter = new(format, target);
        for (int at = 0; at < noise.Length; at += 441 * 2)
        {
            converter.Convert(
                Frame(Floats(noise.AsSpan(at, 441 * 2).ToArray()), format, at / 2 / 44_100.0),
                chunked
            );
        }

        int compared = Math.Min(whole.Samples.Count, chunked.Samples.Count);
        Assert.IsGreaterThan(90_000, compared);
        for (int i = 0; i < compared; i++)
        {
            Assert.AreEqual(whole.Samples[i], chunked.Samples[i], 1e-6f, $"sample {i}");
        }
    }

    [TestMethod]
    public void Target_KeepsWhatIsTakenAndPicksTheNearestRateAbove()
    {
        AudioConstraints opus = new(
            [48_000, 24_000, 16_000, 12_000, 8_000],
            [SampleFormat.F32, SampleFormat.S16],
            2
        );

        Assert.AreEqual(
            new AudioFormat(SampleFormat.F32, 48_000, 2),
            AudioConverter.Target(new AudioFormat(SampleFormat.F32, 44_100, 6), opus)
        );
        Assert.AreEqual(
            new AudioFormat(SampleFormat.S16, 24_000, 1),
            AudioConverter.Target(new AudioFormat(SampleFormat.S16, 22_050, 1), opus)
        );
        Assert.AreEqual(
            new AudioFormat(SampleFormat.F32, 48_000, 2),
            AudioConverter.Target(new AudioFormat(SampleFormat.F32, 96_000, 2), opus)
        );
    }

    private static AudioFrame Frame(byte[] samples, AudioFormat format, double seconds) =>
        new(
            samples,
            format,
            MediaTimestamp.Captured(MediaTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)))
        );

    private static byte[] Floats(params float[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
        }

        return bytes;
    }

    // Collects output as floats, with each frame's timestamp and where it starts.
    private sealed class Collector : IAudioFrameConsumer
    {
        private readonly List<(int Start, double Seconds)> _frames = [];

        public List<float> Samples { get; } = [];

        public void OnFrame(in AudioFrame frame)
        {
            _frames.Add(
                (
                    Samples.Count / frame.Format.Channels,
                    frame.Timestamp.Time.ToTimeSpan().TotalSeconds
                )
            );
            for (int i = 0; i < frame.Samples.Length; i += frame.Format.BytesPerSample)
            {
                Samples.Add(
                    frame.Format.SampleFormat == SampleFormat.S16
                        ? BinaryPrimitives.ReadInt16LittleEndian(frame.Samples[i..]) / 32768f
                        : BinaryPrimitives.ReadSingleLittleEndian(frame.Samples[i..])
                );
            }
        }

        // The time of a mono output sample, from the frame it is in.
        public double TimeOf(int index, int rate)
        {
            (int start, double seconds) = _frames.Last(f => f.Start <= index);
            return seconds + ((double)(index - start) / rate);
        }
    }
}
