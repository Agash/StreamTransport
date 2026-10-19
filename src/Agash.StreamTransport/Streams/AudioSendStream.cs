using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Streams;

/// <summary>
/// Sends one audio source to a peer. Audio encodes on the source's own callback: a codec frame costs far
/// less than the audio it carries, and packets leave in capture order with no queue to add delay.
/// </summary>
internal sealed class AudioSendStream : IAudioFrameConsumer, IEncodedAudioConsumer, IDisposable
{
    private readonly IAudioEncoder _encoder;
    private readonly IAudioFrameConsumer _input;
    private int _framesSent;
    private readonly EncodedAudioSink _send;
    private readonly StreamTransportMetrics _metrics;
    private readonly AudioCodecId _codec;
    private readonly Lock _gate = new();
    private readonly IDisposable _connection;
    private bool _disposed;

    // The audio taken by the encoder and not yet in a packet: its squared samples, sample count and length.
    private double _pendingSquares;
    private long _pendingSamples;
    private TimeSpan _pendingDuration;

    /// <summary>Opens an encoder for the source's format and connects the source.</summary>
    /// <param name="source">The audio source.</param>
    /// <param name="format">The negotiated codec and its parameters.</param>
    /// <param name="registry">Where the encoder comes from.</param>
    /// <param name="options">The session options.</param>
    /// <param name="send">Where encoded packets go.</param>
    /// <param name="metrics">The library's instruments.</param>
    public AudioSendStream(
        IAudioSource source,
        AudioCodecFormat format,
        MediaCodecRegistry registry,
        MediaSessionOptions options,
        EncodedAudioSink send,
        StreamTransportMetrics metrics
    )
    {
        _send = send;
        _metrics = metrics;
        _codec = format.Codec;

        // A source the encoder does not take as it is goes through a converter to the nearest format it
        // does: a 44.1 kHz microphone into 48 kHz Opus.
        AudioFormat input = source.Format;
        AudioConverter? converter = null;
        if (registry.QueryAudioEncoder(format) is { } info && !info.Input.Accepts(input))
        {
            converter = new AudioConverter(input, AudioConverter.Target(input, info.Input));
            input = converter.Output;
        }

        _encoder = registry.TryCreateAudioEncoder(
            new AudioEncoderConfiguration(
                format,
                input,
                options.AudioBitsPerSecond,
                Content: options.AudioContent,
                ExpectedLossPercent: options.AudioExpectedLossPercent
            ),
            out IAudioEncoder? encoder
        )
            ? encoder
            : throw new InvalidOperationException(
                $"No registered encoder encodes {format.Codec} from {input}."
            );
        IAudioFrameConsumer encode = new EncoderInput(this);
        _input = converter?.Feeding(encode) ?? encode;
        try
        {
            _connection = source.Connect(this);
        }
        catch
        {
            _encoder.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public void OnFrame(in AudioFrame frame)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _input.OnFrame(in frame);
            }
        }
    }

    /// <summary>Encoded frames handed to the link.</summary>
    public int FramesSent => Volatile.Read(ref _framesSent);

    /// <inheritdoc/>
    public void OnEncoded(in EncodedAudioFrame frame)
    {
        // Counted before it is handed over, so whoever the hand-over wakes sees it counted.
        Interlocked.Increment(ref _framesSent);
        _send(
            new EncodedAudioFrame(frame.Data, frame.Codec, frame.Timestamp, frame.Duration)
            {
                Level = frame.Level ?? TakeLevel(frame.Duration),
            }
        );
        _metrics.AudioFramesSent.Add(1, StreamTransportMetrics.Codec(_codec));
    }

    // RFC 6464 section 3: the RMS of the samples the packet encodes. The encoder takes audio in its own
    // frames, so the packet's share of what it took is its duration's share.
    private AudioLevel TakeLevel(TimeSpan duration)
    {
        if (_pendingSamples == 0 || _pendingDuration <= TimeSpan.Zero)
        {
            return AudioLevel.Silence;
        }

        double share = Math.Min(1, duration / _pendingDuration);
        double squares = _pendingSquares * share;
        long samples = Math.Max(1, (long)(_pendingSamples * share));
        _pendingSquares -= squares;
        _pendingSamples -= samples;
        _pendingDuration -= duration < _pendingDuration ? duration : _pendingDuration;
        return AudioLevel.FromMeanSquare(squares / samples);
    }

    // Frames as the encoder takes them, measured on the way in.
    private sealed class EncoderInput(AudioSendStream stream) : IAudioFrameConsumer
    {
        public void OnFrame(in AudioFrame frame)
        {
            (double squares, long samples) = AudioLevel.Measure(in frame);
            stream._pendingSquares += squares;
            stream._pendingSamples += samples;
            stream._pendingDuration += frame.Duration;
            stream._encoder.Encode(in frame, stream);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _connection.Dispose();
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                _encoder.Dispose();
            }
        }
    }
}
