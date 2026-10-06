using System.Threading.Channels;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.Sync;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.Streams;

/// <summary>
/// Receives one audio stream from a peer. Packets decode in sequence order on a worker; a packet that
/// arrives after its successor was decoded is dropped. A gap is filled before the next packet plays:
/// rebuilt from that packet's redundancy when the decoder can and one packet was lost, concealed
/// otherwise, so the audio timeline has no holes.
/// </summary>
internal sealed partial class AudioReceiveStream : IAudioFrameConsumer, IAsyncDisposable
{
    // The longest gap concealed; a longer outage restarts the timeline at the next packet.
    private static readonly TimeSpan MaxConcealment = TimeSpan.FromMilliseconds(120);

    private readonly AudioCodecFormat _format;
    private readonly IAudioSink _sink;
    private readonly IAudioDecoder _decoder;
    private readonly Playout _playout;
    private readonly StreamTransportMetrics _metrics;
    private readonly ILogger _logger;
    private readonly ArrivalStamps _stamps;
    private readonly Channel<Packet> _packets = Channel.CreateUnbounded<Packet>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private long? _nextSequence;
    private TimeSpan _nextPosition;
    private NtpTime? _playing;
    private TimeSpan _decoded;
    private int _framesDecoded;
    private int _concealed;
    private int _recovered;

    /// <summary>Opens a decoder and starts the worker.</summary>
    /// <param name="format">The negotiated codec and its parameters.</param>
    /// <param name="sink">Where audio goes.</param>
    /// <param name="registry">Where the decoder comes from.</param>
    /// <param name="playout">The session's playout.</param>
    /// <param name="clock">The local media clock.</param>
    /// <param name="metrics">The library's instruments.</param>
    /// <param name="logger">The logger.</param>
    public AudioReceiveStream(
        AudioCodecFormat format,
        IAudioSink sink,
        MediaCodecRegistry registry,
        Playout playout,
        MediaClock clock,
        StreamTransportMetrics metrics,
        ILogger logger
    )
    {
        _format = format;
        _metrics = metrics;
        _sink = sink;
        _playout = playout;
        _logger = logger;
        _stamps = new ArrivalStamps(clock);
        _decoder = registry.TryCreateAudioDecoder(format, out IAudioDecoder? decoder)
            ? decoder
            : throw new InvalidOperationException($"No registered decoder decodes {format.Codec}.");
        if (!sink.Constraints.Accepts(_decoder.Output))
        {
            _decoder.Dispose();
            throw new InvalidOperationException(
                $"The {format.Codec} decoder produces {_decoder.Output}, which the audio sink does not take."
            );
        }

        _worker = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>Gaps filled by concealment.</summary>
    public int Concealed => Volatile.Read(ref _concealed);

    /// <summary>Lost packets rebuilt from the packet after them.</summary>
    public int Recovered => Volatile.Read(ref _recovered);

    /// <summary>Received frames decoded.</summary>
    public int FramesDecoded => Volatile.Read(ref _framesDecoded);

    /// <summary>Takes a packet from the transport, on its receive thread; the stream owns it.</summary>
    /// <param name="packet">The packet.</param>
    /// <param name="sequence">Its number in the stream.</param>
    /// <param name="position">Where it starts on the stream's timeline.</param>
    /// <param name="capture">When the sender captured its first sample, when known.</param>
    public void OnPacket(
        EncodedFrameBuffer packet,
        long sequence,
        TimeSpan position,
        NtpTime? capture
    )
    {
        Packet queued = new(packet, sequence, position, _stamps.Stamp(capture), capture);
        if (!_packets.Writer.TryWrite(queued))
        {
            packet.Dispose();
        }
    }

    /// <inheritdoc/>
    public void OnFrame(in AudioFrame frame)
    {
        _decoded += TimeSpan.FromTicks(
            frame.SampleCount * TimeSpan.TicksPerSecond / frame.Format.SampleRate
        );
        Interlocked.Increment(ref _framesDecoded);
        _metrics.AudioFramesDecoded.Add(1, StreamTransportMetrics.Codec(_format.Codec));
        _playout.Audio(in frame, _playing, _sink);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _packets.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _worker.ConfigureAwait(false);
        while (_packets.Reader.TryRead(out Packet left))
        {
            left.Data.Dispose();
        }

        _decoder.Dispose();
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (
                Packet packet in _packets
                    .Reader.ReadAllAsync(cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                using (packet.Data)
                {
                    try
                    {
                        Take(packet);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        // A packet the decoder rejects is lost; the next one conceals the gap.
                        LogDecodeFailed(exception, packet.Sequence);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Deliberately not logged: cancellation is how the stream stops.
        }
    }

    private void Take(in Packet packet)
    {
        if (_nextSequence is { } expected)
        {
            long ahead = packet.Sequence - expected;
            if (ahead < 0)
            {
                return;
            }

            if (ahead > 0)
            {
                FillGap(packet, lost: (int)Math.Min(ahead, int.MaxValue));
            }
        }

        EncodedAudioFrame frame = new(packet.Data.Span, _format.Codec, packet.Stamp, TimeSpan.Zero);
        _playing = packet.Capture;
        _decoded = TimeSpan.Zero;
        _decoder.Decode(in frame, this);
        _nextSequence = packet.Sequence + 1;
        _nextPosition = packet.Position + _decoded;
    }

    // Fills the audio of lost packets that ended where the next packet begins.
    private void FillGap(in Packet next, int lost)
    {
        TimeSpan gap = next.Position - _nextPosition;
        if (gap <= TimeSpan.Zero || gap > MaxConcealment)
        {
            return;
        }

        // The filled audio was captured just before the packet that ends the gap.
        _playing = next.Capture is { } capture ? capture + -gap : null;
        MediaTimestamp stamp = new(next.Stamp.Time - gap, TimestampKind.Observation);
        if (lost == 1 && _decoder is IAudioLossRecovery recovery)
        {
            EncodedAudioFrame frame = new(next.Data.Span, _format.Codec, next.Stamp, TimeSpan.Zero);
            recovery.Recover(in frame, gap, stamp, this);
            Interlocked.Increment(ref _recovered);
            _metrics.AudioFramesRepaired.Add(
                1,
                StreamTransportMetrics.Codec(_format.Codec),
                StreamTransportMetrics.Reason("recovered")
            );
        }
        else
        {
            _decoder.Conceal(gap, stamp, this);
            Interlocked.Increment(ref _concealed);
            _metrics.AudioFramesRepaired.Add(
                1,
                StreamTransportMetrics.Codec(_format.Codec),
                StreamTransportMetrics.Reason("concealed")
            );
        }
    }

    [LoggerMessage(
        2060,
        LogLevel.Debug,
        "Audio packet {Sequence} failed to decode; it is concealed."
    )]
    private partial void LogDecodeFailed(Exception exception, long sequence);

    private readonly record struct Packet(
        EncodedFrameBuffer Data,
        long Sequence,
        TimeSpan Position,
        MediaTimestamp Stamp,
        NtpTime? Capture
    );
}
