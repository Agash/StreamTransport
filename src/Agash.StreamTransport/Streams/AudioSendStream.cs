using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Streams;

/// <summary>
/// Sends one audio source to a peer. Audio encodes on the source's own callback: a codec frame costs far
/// less than the audio it carries, and packets leave in capture order with no queue to add delay.
/// </summary>
internal sealed class AudioSendStream : IAudioFrameConsumer, IEncodedAudioConsumer, IDisposable
{
    private readonly IAudioEncoder _encoder;
    private int _framesSent;
    private readonly EncodedAudioSink _send;
    private readonly StreamTransportMetrics _metrics;
    private readonly AudioCodecId _codec;
    private readonly Lock _gate = new();
    private readonly IDisposable _connection;
    private bool _disposed;

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
        _encoder = registry.TryCreateAudioEncoder(
            new AudioEncoderConfiguration(
                format,
                source.Format,
                options.AudioBitsPerSecond,
                Content: options.AudioContent,
                ExpectedLossPercent: options.AudioExpectedLossPercent
            ),
            out IAudioEncoder? encoder
        )
            ? encoder
            : throw new InvalidOperationException(
                $"No registered encoder encodes {format.Codec} from {source.Format}."
            );
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
                _encoder.Encode(in frame, this);
            }
        }
    }

    /// <summary>Encoded frames handed to the link.</summary>
    public int FramesSent => Volatile.Read(ref _framesSent);

    /// <inheritdoc/>
    public void OnEncoded(in EncodedAudioFrame frame)
    {
        _send(in frame);
        Interlocked.Increment(ref _framesSent);
        _metrics.AudioFramesSent.Add(1, StreamTransportMetrics.Codec(_codec));
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
