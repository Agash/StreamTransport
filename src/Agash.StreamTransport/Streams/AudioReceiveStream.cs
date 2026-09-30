using System.Buffers;
using System.Threading.Channels;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.Sync;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;
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
    private readonly ILogger _logger;
    private readonly ClockRate _rtpRate;
    private readonly RtpClockAligner _aligner;
    private readonly ArrivalStamps _stamps;
    private readonly Channel<Packet> _packets = Channel.CreateUnbounded<Packet>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private ushort? _nextSequence;
    private uint _nextTimestamp;
    private NtpTime? _playing;
    private long _decodedTicks;
    private int _concealed;
    private int _recovered;

    /// <summary>Opens a decoder and starts the worker.</summary>
    /// <param name="format">The negotiated codec and its parameters.</param>
    /// <param name="payloadFormat">The RTP payload format, for its clock rate.</param>
    /// <param name="sink">Where audio goes.</param>
    /// <param name="registry">Where the decoder comes from.</param>
    /// <param name="playout">The session's playout.</param>
    /// <param name="clock">The local media clock.</param>
    /// <param name="logger">The logger.</param>
    public AudioReceiveStream(
        AudioCodecFormat format,
        RtpPayloadFormat payloadFormat,
        IAudioSink sink,
        MediaCodecRegistry registry,
        Playout playout,
        MediaClock clock,
        ILogger logger
    )
    {
        _format = format;
        _sink = sink;
        _playout = playout;
        _logger = logger;
        _rtpRate = new ClockRate(payloadFormat.ClockRate);
        _aligner = new RtpClockAligner(_rtpRate);
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

    /// <summary>Takes one RTP packet of the stream, on the transport's receive thread.</summary>
    /// <param name="header">The packet's header.</param>
    /// <param name="payload">Its payload, borrowed for the call.</param>
    public void OnPacket(in RtpHeader header, ReadOnlySpan<byte> payload)
    {
        if (header.AbsoluteCaptureTimeNtp is { } ntp and not 0)
        {
            _aligner.Record(new NtpTime(ntp), header.Timestamp);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(payload.Length, 1));
        payload.CopyTo(buffer);
        NtpTime? capture = _aligner.TryGetCapture(header.Timestamp, out NtpTime at) ? at : null;
        Packet packet = new(
            new EncodedFrameBuffer(buffer, payload.Length),
            header.SequenceNumber,
            header.Timestamp,
            _stamps.Stamp(capture),
            capture
        );
        if (!_packets.Writer.TryWrite(packet))
        {
            packet.Data.Dispose();
        }
    }

    /// <inheritdoc/>
    public void OnFrame(in AudioFrame frame)
    {
        _decodedTicks += _rtpRate.ToTicks(
            TimeSpan.FromTicks(
                frame.SampleCount * TimeSpan.TicksPerSecond / frame.Format.SampleRate
            )
        );
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
            short ahead = unchecked((short)(packet.Sequence - expected));
            if (ahead < 0)
            {
                return;
            }

            if (ahead > 0)
            {
                FillGap(packet, lost: ahead);
            }
        }

        EncodedAudioFrame frame = new(packet.Data.Span, _format.Codec, packet.Stamp, TimeSpan.Zero);
        _playing = packet.Capture;
        _decodedTicks = 0;
        _decoder.Decode(in frame, this);
        _nextSequence = unchecked((ushort)(packet.Sequence + 1));
        _nextTimestamp = unchecked(packet.Timestamp + (uint)_decodedTicks);
    }

    // Fills the audio of lost packets that ended where the next packet begins.
    private void FillGap(in Packet next, int lost)
    {
        var gap = _rtpRate.ToTimeSpan(unchecked((int)(next.Timestamp - _nextTimestamp)));
        if (gap <= TimeSpan.Zero || gap > MaxConcealment)
        {
            return;
        }

        _playing = _aligner.TryGetCapture(_nextTimestamp, out NtpTime at) ? at : null;
        MediaTimestamp stamp = new(next.Stamp.Time - gap, TimestampKind.Observation);
        if (lost == 1 && _decoder is IAudioLossRecovery recovery)
        {
            EncodedAudioFrame frame = new(next.Data.Span, _format.Codec, next.Stamp, TimeSpan.Zero);
            recovery.Recover(in frame, gap, stamp, this);
            Interlocked.Increment(ref _recovered);
        }
        else
        {
            _decoder.Conceal(gap, stamp, this);
            Interlocked.Increment(ref _concealed);
        }
    }

    [LoggerMessage(
        2060,
        LogLevel.Debug,
        "Audio packet {Sequence} failed to decode; it is concealed."
    )]
    private partial void LogDecodeFailed(Exception exception, ushort sequence);

    private readonly record struct Packet(
        EncodedFrameBuffer Data,
        ushort Sequence,
        uint Timestamp,
        MediaTimestamp Stamp,
        NtpTime? Capture
    );
}
