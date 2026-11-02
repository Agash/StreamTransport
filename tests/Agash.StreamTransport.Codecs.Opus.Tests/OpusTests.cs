using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Codecs.Opus.Tests;

[TestClass]
public sealed class OpusTests
{
    private static readonly AudioFormat Stereo = new(SampleFormat.F32, 48_000, 2);

    [TestMethod]
    public void Encode_TenMillisecondChunks_GivesTwentyMillisecondPacketsAtTheirCaptureTimes()
    {
        using IAudioEncoder encoder = Encoder(Stereo);
        Packets packets = new();
        for (int chunk = 0; chunk < 100; chunk++)
        {
            Feed(encoder, Stereo, chunk, samples: 480, packets);
        }

        Assert.HasCount(50, packets.All);
        for (int i = 0; i < packets.All.Count; i++)
        {
            Assert.AreEqual(TimeSpan.FromMilliseconds(20), packets.All[i].Duration);
            Assert.AreEqual(
                Start + TimeSpan.FromMilliseconds(20 * i),
                packets.All[i].Timestamp.Time
            );
            Assert.AreEqual(TimestampKind.Capture, packets.All[i].Timestamp.Kind);
        }
    }

    [TestMethod]
    public void Decode_StereoTone_KeepsItsLevelInBothChannels()
    {
        using IAudioEncoder encoder = Encoder(Stereo);
        Packets packets = new();
        for (int chunk = 0; chunk < 100; chunk++)
        {
            Feed(encoder, Stereo, chunk, samples: 480, packets);
        }

        using var decoder = (OpusAudioDecoder)Decoders.Create(Format);
        Pcm pcm = new();
        foreach ((byte[] data, MediaTimestamp timestamp, TimeSpan duration) in packets.All)
        {
            decoder.Decode(
                new EncodedAudioFrame(data, AudioCodecId.Opus, timestamp, duration),
                pcm
            );
        }

        Assert.AreEqual(48_000 * 2, pcm.Samples.Count);
        // Skip the encoder's warm-up; a 0.5 amplitude sine has an RMS of about 0.354.
        (double left, double right) = pcm.Rms(skip: 4_800);
        Assert.AreEqual(0.354, left, 0.05);
        Assert.AreEqual(0.354, right, 0.05);
        Assert.AreEqual(Start, pcm.FirstTimestamp!.Value.Time);
    }

    [TestMethod]
    public void Decode_MonoStream_ComesOutStereo()
    {
        AudioFormat mono = new(SampleFormat.S16, 48_000, 1);
        using IAudioEncoder encoder = Encoder(mono);
        Packets packets = new();
        for (int chunk = 0; chunk < 50; chunk++)
        {
            Feed(encoder, mono, chunk, samples: 960, packets);
        }

        using var decoder = (OpusAudioDecoder)Decoders.Create(Format);
        Pcm pcm = new();
        foreach ((byte[] data, MediaTimestamp timestamp, TimeSpan duration) in packets.All)
        {
            decoder.Decode(
                new EncodedAudioFrame(data, AudioCodecId.Opus, timestamp, duration),
                pcm
            );
        }

        (double left, double right) = pcm.Rms(skip: 4_800);
        Assert.AreEqual(left, right, 0.01, "Mono decodes to the same signal in both channels.");
        Assert.IsGreaterThan(0.2, left);
    }

    [TestMethod]
    public void Conceal_LostPacket_FillsItsDuration()
    {
        using IAudioEncoder encoder = Encoder(Stereo);
        Packets packets = new();
        for (int chunk = 0; chunk < 10; chunk++)
        {
            Feed(encoder, Stereo, chunk, samples: 480, packets);
        }

        using var decoder = (OpusAudioDecoder)Decoders.Create(Format);
        Pcm pcm = new();
        (byte[] first, MediaTimestamp at, TimeSpan duration) = packets.All[0];
        decoder.Decode(new EncodedAudioFrame(first, AudioCodecId.Opus, at, duration), pcm);
        int before = pcm.Samples.Count;

        MediaTimestamp lost = packets.All[1].Timestamp;
        decoder.Conceal(TimeSpan.FromMilliseconds(20), lost, pcm);

        Assert.AreEqual(960 * 2, pcm.Samples.Count - before);
        Assert.AreEqual(lost, pcm.LastTimestamp);
    }

    [TestMethod]
    public void Recover_FromTheNextPacket_ProducesTheLostDuration()
    {
        using IAudioEncoder encoder = Encoder(Stereo, expectedLossPercent: 20);
        Packets packets = new();
        for (int chunk = 0; chunk < 20; chunk++)
        {
            Feed(encoder, Stereo, chunk, samples: 480, packets);
        }

        using var decoder = (OpusAudioDecoder)Decoders.Create(Format);
        Pcm pcm = new();
        for (int i = 0; i < 4; i++)
        {
            (byte[] data, MediaTimestamp at, TimeSpan duration) = packets.All[i];
            decoder.Decode(new EncodedAudioFrame(data, AudioCodecId.Opus, at, duration), pcm);
        }

        int before = pcm.Samples.Count;
        (byte[] next, MediaTimestamp nextAt, TimeSpan nextDuration) = packets.All[5];
        decoder.Recover(
            new EncodedAudioFrame(next, AudioCodecId.Opus, nextAt, nextDuration),
            TimeSpan.FromMilliseconds(20),
            packets.All[4].Timestamp,
            pcm
        );

        Assert.AreEqual(960 * 2, pcm.Samples.Count - before);
        Assert.AreEqual(packets.All[4].Timestamp, pcm.LastTimestamp);
    }

    [TestMethod]
    public void Reconfigure_LowerRate_ShrinksThePackets()
    {
        using IAudioEncoder encoder = Encoder(Stereo, bitsPerSecond: 128_000);
        Packets packets = new();
        for (int chunk = 0; chunk < 100; chunk++)
        {
            if (chunk == 50)
            {
                encoder.Reconfigure(24_000);
            }

            Feed(encoder, Stereo, chunk, samples: 480, packets, noise: true);
        }

        double high = packets.All.Skip(5).Take(20).Average(static p => p.Data.Length);
        double low = packets.All.Skip(30).Take(20).Average(static p => p.Data.Length);
        Assert.IsGreaterThan(2.5, high / low, $"{high:F0} bytes before, {low:F0} after.");
    }

    private static readonly MediaTime Start = new(5_000_000_000);
    private static readonly AudioCodecFormat Format = new(AudioCodecId.Opus);
    private static readonly OpusAudioDecoderFactory Decoders = new();

    // Encoders come from the factory, as a pipeline makes them.
    private static IAudioEncoder Encoder(
        AudioFormat input,
        int bitsPerSecond = 64_000,
        int expectedLossPercent = 10
    ) =>
        new OpusAudioEncoderFactory().Create(
            new AudioEncoderConfiguration(
                Format,
                input,
                bitsPerSecond,
                ExpectedLossPercent: expectedLossPercent
            )
        );

    // A chunk of a 1 kHz sine at half scale (or noise), timestamped as captured.
    private static void Feed(
        IAudioEncoder encoder,
        AudioFormat format,
        int chunk,
        int samples,
        Packets packets,
        bool noise = false
    )
    {
        float[] values = new float[samples * format.Channels];
        Random random = new(chunk);
        for (int s = 0; s < samples; s++)
        {
            long index = ((long)chunk * samples) + s;
            float value = noise
                ? (float)(random.NextDouble() - 0.5)
                : 0.5f * MathF.Sin(2 * MathF.PI * 1000 * index / format.SampleRate);
            for (int c = 0; c < format.Channels; c++)
            {
                values[(s * format.Channels) + c] = value;
            }
        }

        byte[] bytes =
            format.SampleFormat == SampleFormat.F32
                ? MemoryMarshal.AsBytes(values.AsSpan()).ToArray()
                : MemoryMarshal
                    .AsBytes(values.Select(static v => (short)(v * 32767)).ToArray().AsSpan())
                    .ToArray();
        MediaTime at = Start + new ClockRate(format.SampleRate).ToTimeSpan((long)chunk * samples);
        encoder.Encode(new AudioFrame(bytes, format, MediaTimestamp.Captured(at)), packets);
    }

    private sealed class Packets : IEncodedAudioConsumer
    {
        public List<(byte[] Data, MediaTimestamp Timestamp, TimeSpan Duration)> All { get; } = [];

        public void OnEncoded(in EncodedAudioFrame frame) =>
            All.Add((frame.Data.ToArray(), frame.Timestamp, frame.Duration));
    }

    private sealed class Pcm : IAudioFrameConsumer
    {
        public List<float> Samples { get; } = [];

        public MediaTimestamp? FirstTimestamp { get; private set; }

        public MediaTimestamp? LastTimestamp { get; private set; }

        public void OnFrame(in AudioFrame frame)
        {
            Assert.AreEqual(new AudioFormat(SampleFormat.F32, 48_000, 2), frame.Format);
            Samples.AddRange(MemoryMarshal.Cast<byte, float>(frame.Samples).ToArray());
            FirstTimestamp ??= frame.Timestamp;
            LastTimestamp = frame.Timestamp;
        }

        public (double Left, double Right) Rms(int skip)
        {
            double left = 0;
            double right = 0;
            int count = 0;
            for (int i = skip * 2; i + 1 < Samples.Count; i += 2, count++)
            {
                left += Samples[i] * Samples[i];
                right += Samples[i + 1] * Samples[i + 1];
            }

            return (Math.Sqrt(left / count), Math.Sqrt(right / count));
        }
    }

    [TestMethod]
    public void Factories_AnswerForOpusOnly()
    {
        OpusAudioEncoderFactory encoders = new();
        AudioCodecFormat other = new(new AudioCodecId("G722"));

        Assert.IsNotNull(encoders.QueryCapabilities(Format));
        Assert.IsNull(encoders.QueryCapabilities(other));
        Assert.IsNull(Decoders.QueryCapabilities(other));
        Assert.IsTrue(encoders.QueryCapabilities(Format)!.Input.Accepts(Stereo));
        Assert.IsFalse(
            encoders
                .QueryCapabilities(Format)!
                .Input.Accepts(new AudioFormat(SampleFormat.F32, 44_100, 2))
        );
        Assert.ThrowsExactly<ArgumentException>(() => Decoders.Create(other));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            encoders.Create(
                new AudioEncoderConfiguration(
                    Format,
                    new AudioFormat(SampleFormat.F32, 44_100, 2),
                    64_000
                )
            )
        );
    }

    [TestMethod]
    public void Decoder_RecoversThroughTheLossRecoveryContract() =>
        Assert.IsInstanceOfType<IAudioLossRecovery>(Decoders.Create(Format));
}
