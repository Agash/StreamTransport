using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Concentus;
using Concentus.Enums;

namespace Agash.StreamTransport.Codecs.Opus;

/// <summary>What the Opus encoder is tuned for.</summary>
public enum OpusTuning
{
    /// <summary>Speech: the SILK layer, voice band limits.</summary>
    Voice,

    /// <summary>Music and mixed content: full band.</summary>
    Music,
}

/// <summary>How an <see cref="OpusAudioEncoder"/> is set up.</summary>
public sealed record OpusEncoderOptions
{
    /// <summary>The target bit rate.</summary>
    public int BitsPerSecond { get; init; } = 64_000;

    /// <summary>The duration of each packet: 10, 20, 40 or 60 ms.</summary>
    public TimeSpan FrameDuration { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>What the encoder is tuned for.</summary>
    public OpusTuning Tuning { get; init; } = OpusTuning.Music;

    /// <summary>
    /// The packet loss the in-band forward error correction plans for, in percent; zero turns it off.
    /// With it on, each packet carries a coarse copy of the one before, so a receiver recovers a lost
    /// packet from the next.
    /// </summary>
    public int ExpectedLossPercent { get; init; } = 10;

    /// <summary>The encoder's effort, 0 (fastest) to 10 (best).</summary>
    public int Complexity { get; init; } = 7;
}

/// <summary>
/// An Opus encoder. Capture frames of any length go in; packets of <see cref="OpusEncoderOptions.FrameDuration"/>
/// come out as they fill, each stamped with the capture time of its first sample.
/// </summary>
public sealed class OpusAudioEncoder : IAudioEncoder
{
    // Opus' largest packet is 1275 bytes per frame; 4000 covers any frame duration.
    private const int MaxPacketBytes = 4000;

    private readonly IOpusEncoder _encoder;
    private readonly int _frameSamples;
    private readonly float[] _pending;
    private readonly byte[] _packet = new byte[MaxPacketBytes];
    private readonly TimeSpan _frameDuration;
    private readonly Lock _gate = new();
    private int _pendingSamples;
    private MediaTimestamp _pendingStart;
    private bool _disposed;

    /// <summary>An encoder for PCM in a format.</summary>
    /// <param name="input">The PCM format: 8, 12, 16, 24 or 48 kHz, one or two channels.</param>
    /// <param name="options">The options; defaults when null.</param>
    public OpusAudioEncoder(AudioFormat input, OpusEncoderOptions? options = null)
    {
        if (input.SampleRate is not (8_000 or 12_000 or 16_000 or 24_000 or 48_000))
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                input.SampleRate,
                "Opus encodes 8, 12, 16, 24 or 48 kHz."
            );
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(input.Channels, 1, nameof(input));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input.Channels, 2, nameof(input));
        options ??= new OpusEncoderOptions();
        if (options.FrameDuration.TotalMilliseconds is not (10 or 20 or 40 or 60))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.FrameDuration,
                "Opus packets here are 10, 20, 40 or 60 ms."
            );
        }

        Input = input;
        _frameDuration = options.FrameDuration;
        _frameSamples = (int)(input.SampleRate * options.FrameDuration.TotalSeconds);
        _pending = new float[_frameSamples * input.Channels];
        _encoder = OpusCodecFactory.CreateEncoder(
            input.SampleRate,
            input.Channels,
            options.Tuning == OpusTuning.Voice
                ? OpusApplication.OPUS_APPLICATION_VOIP
                : OpusApplication.OPUS_APPLICATION_AUDIO
        );
        _encoder.Bitrate = options.BitsPerSecond;
        _encoder.Complexity = Math.Clamp(options.Complexity, 0, 10);
        _encoder.UseInbandFEC = options.ExpectedLossPercent > 0;
        _encoder.PacketLossPercent = Math.Clamp(options.ExpectedLossPercent, 0, 100);
        _encoder.SignalType =
            options.Tuning == OpusTuning.Voice
                ? OpusSignal.OPUS_SIGNAL_VOICE
                : OpusSignal.OPUS_SIGNAL_MUSIC;
    }

    /// <inheritdoc/>
    public AudioFormat Input { get; }

    /// <inheritdoc/>
    public void Encode(in AudioFrame frame, IEncodedAudioConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        if (frame.Format != Input)
        {
            throw new ArgumentException(
                $"The encoder takes {Input}; the frame is {frame.Format}.",
                nameof(frame)
            );
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int channels = Input.Channels;
            int total = frame.SampleCount;
            int taken = 0;
            while (taken < total)
            {
                if (_pendingSamples == 0)
                {
                    _pendingStart = frame.Timestamp with
                    {
                        Time =
                            frame.Timestamp.Time
                            + new ClockRate(Input.SampleRate).ToTimeSpan(taken),
                    };
                }

                int count = Math.Min(total - taken, _frameSamples - _pendingSamples);
                Span<float> into = _pending.AsSpan(_pendingSamples * channels, count * channels);
                ReadOnlySpan<byte> from = frame.Samples.Slice(
                    taken * Input.BytesPerFrame,
                    count * Input.BytesPerFrame
                );
                if (Input.SampleFormat == SampleFormat.F32)
                {
                    MemoryMarshal.Cast<byte, float>(from).CopyTo(into);
                }
                else
                {
                    ReadOnlySpan<short> shorts = MemoryMarshal.Cast<byte, short>(from);
                    for (int i = 0; i < shorts.Length; i++)
                    {
                        into[i] = shorts[i] / 32768f;
                    }
                }

                _pendingSamples += count;
                taken += count;
                if (_pendingSamples == _frameSamples)
                {
                    int length = _encoder.Encode(_pending, _frameSamples, _packet, _packet.Length);
                    consumer.OnEncoded(
                        new EncodedAudioFrame(
                            _packet.AsSpan(0, length),
                            AudioCodecId.Opus,
                            _pendingStart,
                            _frameDuration
                        )
                    );
                    _pendingSamples = 0;
                }
            }
        }
    }

    /// <inheritdoc/>
    public void Reconfigure(int bitsPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bitsPerSecond, 6_000);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _encoder.Bitrate = Math.Min(bitsPerSecond, 510_000);
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
                (_encoder as IDisposable)?.Dispose();
            }
        }
    }
}
