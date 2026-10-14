using Agash.StreamTransport.WebRtc.Rtcp;

namespace Agash.StreamTransport.WebRtc.Rtp;

/// <summary>
/// What a receiver reports about one remote source (RFC 3550 appendix A): the extended highest sequence
/// number, cumulative and interval loss, interarrival jitter, and the timing of the source's last sender
/// report, from which the sender measures the round trip.
/// </summary>
/// <param name="clockRate">The source's RTP clock rate, which jitter is counted in.</param>
internal sealed class RtpReceiveStatistics(int clockRate)
{
    // A jump of more than this ahead, or a packet this far behind, restarts the sequence (appendix A.1).
    private const int MaxDropout = 3000;
    private const int MaxMisorder = 100;

    private bool _started;
    private ushort _maxSequence;
    private uint _cycles;
    private uint _baseSequence;
    private uint _badSequence = uint.MaxValue;
    private long _received;
    private long _expectedPrior;
    private long _receivedPrior;
    private double _jitter;
    private long _lastTransit;
    private bool _hasTransit;
    private uint _lastSenderReport;
    private long _lastSenderReportMicros;

    /// <summary>Folds in an arriving packet.</summary>
    /// <param name="sequence">Its sequence number.</param>
    /// <param name="rtpTimestamp">Its RTP timestamp.</param>
    /// <param name="arrivalMicros">When it arrived, on the receiver's monotonic clock.</param>
    public void OnPacket(ushort sequence, uint rtpTimestamp, long arrivalMicros)
    {
        if (!_started)
        {
            Restart(sequence);
            _started = true;
        }
        else
        {
            ushort delta = (ushort)(sequence - _maxSequence);
            if (delta < MaxDropout)
            {
                // In order, with a permissible gap; a wrap counts another cycle.
                if (sequence < _maxSequence)
                {
                    _cycles += 0x10000;
                }

                _maxSequence = sequence;
            }
            else if (delta <= 0x10000 - MaxMisorder)
            {
                // A large jump: twice in a row, from consecutive numbers, means the source restarted.
                if (sequence == _badSequence)
                {
                    Restart(sequence);
                }
                else
                {
                    _badSequence = (uint)((sequence + 1) & 0xFFFF);
                    return;
                }
            }

            // Otherwise a duplicate or a reordered packet: counted, the highest stays.
        }

        _received++;

        // Interarrival jitter (appendix A.8), in RTP timestamp units.
        long arrival = arrivalMicros * clockRate / 1_000_000;
        long transit = arrival - rtpTimestamp;
        if (_hasTransit)
        {
            double difference = Math.Abs(transit - _lastTransit);
            _jitter += (difference - _jitter) / 16.0;
        }

        _lastTransit = transit;
        _hasTransit = true;
    }

    /// <summary>Notes the source's sender report, which the next report block echoes.</summary>
    /// <param name="ntpTimestamp">The report's NTP timestamp.</param>
    /// <param name="arrivalMicros">When it arrived, on the receiver's monotonic clock.</param>
    public void OnSenderReport(ulong ntpTimestamp, long arrivalMicros)
    {
        _lastSenderReport = (uint)(ntpTimestamp >> 16);
        _lastSenderReportMicros = arrivalMicros;
    }

    /// <summary>The report block for the source as of now, starting the next loss interval.</summary>
    /// <param name="ssrc">The source.</param>
    /// <param name="nowMicros">Now, on the receiver's monotonic clock.</param>
    /// <returns>The block, or null before the first packet.</returns>
    public RtcpReportBlock? Report(uint ssrc, long nowMicros)
    {
        if (!_started)
        {
            return null;
        }

        long extendedMax = _cycles + _maxSequence;
        long expected = extendedMax - _baseSequence + 1;
        long lost = Math.Clamp(expected - _received, -0x800000, 0x7FFFFF);
        long expectedInterval = expected - _expectedPrior;
        long receivedInterval = _received - _receivedPrior;
        long lostInterval = expectedInterval - receivedInterval;
        _expectedPrior = expected;
        _receivedPrior = _received;
        byte fraction =
            expectedInterval == 0 || lostInterval <= 0
                ? (byte)0
                : (byte)Math.Min(255, (lostInterval << 8) / expectedInterval);

        // Delay since the last sender report in 1/65536 s, zero when none arrived.
        uint delay =
            _lastSenderReport == 0
                ? 0
                : (uint)((nowMicros - _lastSenderReportMicros) * 65536 / 1_000_000);
        return new RtcpReportBlock(
            ssrc,
            fraction,
            (int)lost,
            (uint)extendedMax,
            (uint)_jitter,
            _lastSenderReport,
            delay
        );
    }

    private void Restart(ushort sequence)
    {
        _baseSequence = sequence;
        _maxSequence = sequence;
        _badSequence = uint.MaxValue;
        _cycles = 0;
        _received = 0;
        _expectedPrior = 0;
        _receivedPrior = 0;
    }
}
