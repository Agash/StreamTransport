using Agash.StreamTransport;
using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// Encodes and decodes the Opus audio track with full <b>stereo</b> support, driving Concentus (the
/// pure-managed Opus implementation, no native dependency) directly: the encoder matches the source's
/// channel count (a mono mic stays mono on the wire; a stereo source stays stereo) and the decoder is
/// always stereo, so it reconstructs two channels from either. The track is negotiated as <c>opus/48000/2</c>.
/// RTP timing is the per-channel sample count - never the interleaved total, which would run the clock at
/// twice the rate for stereo.
/// </summary>
/// <remarks>
/// Implements both <see cref="IAudioEncoder"/> and <see cref="IAudioDecoder"/>: Opus is a symmetric codec
/// and a session uses a given instance in only one direction (the engine hands out a fresh instance per role).
/// </remarks>
internal sealed class AudioPipeline : IAudioEncoder, IAudioDecoder
{
    private const int SampleRate = 48_000;
    private const int DecoderChannels = 2;

    // Opus' largest frame is 120 ms; at 48 kHz that is 5760 samples per channel.
    private const int MaxDecodeSamplesPerChannel = 5760;

    // Opus packets are small; 4000 bytes is well above the largest a single frame produces.
    private const int MaxPacketBytes = 4000;

    private IOpusEncoder? _encoder;
    private int _encoderChannels;
    private IOpusDecoder? _decoder;

    public AudioPipeline(AudioCodec codec)
    {
        if (codec != AudioCodec.Opus)
        {
            throw new NotSupportedException($"Audio codec {codec} is not supported; only Opus.");
        }
    }

    /// <summary>
    /// Encode a PCM frame to an Opus payload plus its duration (samples per channel) in the 48 kHz track
    /// clock. The encoder is (re)created to match the source's channel count.
    /// </summary>
    public EncodedAudioPacket Encode(AudioFrame frame)
    {
        int channels = Math.Max(1, frame.Channels);
        float[] pcm = ToFloatPcm(frame);
        int samplesPerChannel = pcm.Length / channels;

        if (_encoder is null || _encoderChannels != channels)
        {
            _encoder = OpusCodecFactory.CreateEncoder(SampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
            _encoderChannels = channels;
        }

        byte[] buffer = new byte[MaxPacketBytes];
        int length = _encoder.Encode(pcm, samplesPerChannel, buffer, buffer.Length);
        return new EncodedAudioPacket((uint)samplesPerChannel, buffer[..length]);
    }

    /// <summary>Decode an Opus payload back to an interleaved stereo PCM frame (two channels).</summary>
    public AudioFrame Decode(ReadOnlySpan<byte> payload, long presentationTimeNs)
    {
        _decoder ??= OpusCodecFactory.CreateDecoder(SampleRate, DecoderChannels);

        float[] pcm = new float[MaxDecodeSamplesPerChannel * DecoderChannels];
        int samplesPerChannel = _decoder.Decode(payload, pcm.AsSpan(), MaxDecodeSamplesPerChannel, false);

        int total = samplesPerChannel * DecoderChannels;
        byte[] bytes = new byte[total * sizeof(short)];
        Span<short> samples = MemoryMarshal.Cast<byte, short>(bytes.AsSpan());
        for (int i = 0; i < total; i++)
        {
            samples[i] = (short)Math.Clamp(pcm[i] * 32767f, short.MinValue, short.MaxValue);
        }

        return new AudioFrame(bytes, AudioSampleFormat.S16, SampleRate, DecoderChannels, presentationTimeNs);
    }

    public void Dispose()
    {
        (_encoder as IDisposable)?.Dispose();
        (_decoder as IDisposable)?.Dispose();
    }

    private static float[] ToFloatPcm(AudioFrame frame)
    {
        ReadOnlySpan<byte> bytes = frame.Samples.Span;
        if (frame.Format == AudioSampleFormat.S16)
        {
            ReadOnlySpan<short> shorts = MemoryMarshal.Cast<byte, short>(bytes);
            float[] pcm = new float[shorts.Length];
            for (int i = 0; i < shorts.Length; i++)
            {
                pcm[i] = shorts[i] / 32768f;
            }

            return pcm;
        }

        return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
    }
}
