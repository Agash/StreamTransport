using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Concentus;

namespace Agash.StreamTransport.Codecs.Opus;

/// <summary>
/// An Opus decoder producing 48 kHz stereo float PCM, whatever the channel count and rate of the
/// encoder: Opus decodes any stream at any of its rates and mixes or duplicates channels as asked.
/// </summary>
public sealed class OpusAudioDecoder : IAudioDecoder, IAudioLossRecovery
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;

    // Opus' longest packet is 120 ms.
    private const int MaxSamples = SampleRate * 120 / 1000;

    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
    private readonly float[] _pcm = new float[MaxSamples * Channels];
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <inheritdoc/>
    public AudioFormat Output { get; } = new(SampleFormat.F32, SampleRate, Channels);

    /// <inheritdoc/>
    public void Decode(in EncodedAudioFrame frame, IAudioFrameConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        if (frame.Codec != AudioCodecId.Opus)
        {
            throw new ArgumentException(
                $"This decoder decodes Opus, not {frame.Codec}.",
                nameof(frame)
            );
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int samples = _decoder.Decode(frame.Data, _pcm, MaxSamples, decode_fec: false);
            Deliver(samples, frame.Timestamp, consumer);
        }
    }

    /// <inheritdoc/>
    public void Conceal(TimeSpan duration, MediaTimestamp timestamp, IAudioFrameConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Opus conceals in whole 2.5 ms steps, at most 120 ms per call.
            int step = SampleRate / 400;
            int wanted = (int)
                Math.Clamp(new ClockRate(SampleRate).ToTicks(duration), step, MaxSamples);
            int samples = _decoder.Decode([], _pcm, wanted / step * step, decode_fec: false);
            Deliver(samples, timestamp, consumer);
        }
    }

    /// <inheritdoc/>
    public void Recover(
        in EncodedAudioFrame next,
        TimeSpan lostDuration,
        MediaTimestamp lostTimestamp,
        IAudioFrameConsumer consumer
    )
    {
        ArgumentNullException.ThrowIfNull(consumer);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int wanted = (int)
                Math.Clamp(
                    new ClockRate(SampleRate).ToTicks(lostDuration),
                    SampleRate / 400,
                    MaxSamples
                );
            int samples = _decoder.Decode(next.Data, _pcm, wanted, decode_fec: true);
            Deliver(samples, lostTimestamp, consumer);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                (_decoder as IDisposable)?.Dispose();
            }
        }
    }

    private void Deliver(int samples, MediaTimestamp timestamp, IAudioFrameConsumer consumer)
    {
        if (samples <= 0)
        {
            return;
        }

        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(_pcm.AsSpan(0, samples * Channels));
        consumer.OnFrame(new AudioFrame(bytes, Output, timestamp));
    }
}
