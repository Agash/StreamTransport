using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Concentus;
using Concentus.Enums;

namespace Agash.StreamTransport.Codecs.Opus;

/// <summary>Settings particular to Opus, beyond an <see cref="AudioEncoderConfiguration"/>.</summary>
public sealed record OpusOptions
{
    /// <summary>The encoder's effort, 0 (fastest) to 10 (best).</summary>
    public int Complexity { get; init; } = 7;
}

/// <summary>
/// An Opus encoder. Capture frames of any length go in; packets of <see cref="AudioEncoderConfiguration.FrameDuration"/>
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

    /// <summary>An encoder set up by a configuration.</summary>
    /// <param name="configuration">
    /// The Opus format, the PCM (8, 12, 16, 24 or 48 kHz, one or two channels), the rate, and a frame
    /// duration of 10, 20, 40 or 60 ms; zero means 20 ms.
    /// </param>
    /// <param name="options">Opus settings; defaults when null.</param>
    public OpusAudioEncoder(AudioEncoderConfiguration configuration, OpusOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Format.Codec != AudioCodecId.Opus)
        {
            throw new ArgumentException(
                $"This encoder encodes Opus; the configuration asks for {configuration.Format.Codec}.",
                nameof(configuration)
            );
        }

        AudioFormat input = configuration.Input;
        if (!OpusAudioEncoderFactory.Constraints.Accepts(input))
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                input,
                "Opus encodes 8, 12, 16, 24 or 48 kHz, one or two channels, as F32 or S16."
            );
        }

        TimeSpan duration =
            configuration.FrameDuration == TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(20)
                : configuration.FrameDuration;
        if (duration.TotalMilliseconds is not (10 or 20 or 40 or 60))
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                duration,
                "Opus packets here are 10, 20, 40 or 60 ms."
            );
        }

        options ??= new OpusOptions();
        bool speech = configuration.Content == AudioContent.Speech;
        Input = input;
        _frameDuration = duration;
        _frameSamples = (int)(input.SampleRate * duration.TotalSeconds);
        _pending = new float[_frameSamples * input.Channels];
        _encoder = OpusCodecFactory.CreateEncoder(
            input.SampleRate,
            input.Channels,
            speech ? OpusApplication.OPUS_APPLICATION_VOIP : OpusApplication.OPUS_APPLICATION_AUDIO
        );
        _encoder.Bitrate = Math.Clamp(configuration.BitsPerSecond, 6_000, 510_000);
        _encoder.Complexity = Math.Clamp(options.Complexity, 0, 10);
        _encoder.UseInbandFEC = configuration.ExpectedLossPercent > 0;
        _encoder.PacketLossPercent = Math.Clamp(configuration.ExpectedLossPercent, 0, 100);
        _encoder.SignalType = speech ? OpusSignal.OPUS_SIGNAL_VOICE : OpusSignal.OPUS_SIGNAL_MUSIC;
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
