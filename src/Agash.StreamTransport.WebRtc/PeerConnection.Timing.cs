using System.Buffers.Binary;
using System.Collections.Concurrent;
using Agash.StreamTransport.WebRtc.Rtp;

namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// Frame timing for <see cref="PeerConnection"/>: the pacer stamps a timing frame's last packet with when
/// it left (the video-timing extension's pacer exit), and the peer's sender reports give the offset between
/// the two wall clocks, which places the peer's capture times on this side's clock.
/// </summary>
public sealed partial class PeerConnection
{
    // At most this many sender report samples are kept; the offset is their median, so one report
    // delayed by a queue does not move it.
    private const int ClockSamples = 9;

    // Timing packets queued at the pacer, by SSRC and sequence number: where their video-timing value is,
    // and the capture time its offsets count from. A few a second; any left behind are cleared in bulk.
    private readonly ConcurrentDictionary<long, (int At, ulong Capture)> _pacerStamps = new();

    private readonly Lock _clockGate = new();
    private readonly Queue<long> _clockSamples = new();
    private long? _clockOffsetTicks;

    /// <summary>
    /// This side's wall clock less the peer's, from the peer's sender reports: each report's NTP time
    /// against when it arrived, less half the round trip (RFC 3550 section 6.4.1, as libwebrtc's remote NTP
    /// time estimator does), the median of the last few. Null before a report has arrived.
    /// </summary>
    public TimeSpan? SenderClockOffset
    {
        get
        {
            lock (_clockGate)
            {
                return _clockOffsetTicks is { } ticks ? TimeSpan.FromTicks(ticks) : null;
            }
        }
    }

    /// <summary>The wall clock now, as an NTP timestamp, on the clock sender reports use.</summary>
    internal ulong NtpNow => NtpAt(NowMicros());

    /// <summary>The time between two NTP timestamps.</summary>
    /// <param name="from">The earlier.</param>
    /// <param name="to">The later.</param>
    /// <returns>The difference; negative when <paramref name="to"/> is earlier.</returns>
    internal static TimeSpan NtpDifference(ulong from, ulong to) =>
        TimeSpan.FromTicks((long)((Int128)(long)(to - from) * TimeSpan.TicksPerSecond >> 32));

    private void NotePacerStamp(uint ssrc, ushort sequence, int at, ulong capture)
    {
        if (_pacerStamps.Count > 256)
        {
            _pacerStamps.Clear();
        }

        _pacerStamps[Key(ssrc, sequence)] = (at, capture);
    }

    // In the pacer's send, before protection: the pacer exit of a timing packet.
    private void StampPacerExit(byte[] packet, uint ssrc, ushort sequence)
    {
        if (
            _pacerStamps.IsEmpty
            || !_pacerStamps.TryRemove(Key(ssrc, sequence), out (int At, ulong Capture) stamp)
        )
        {
            return;
        }

        BinaryPrimitives.WriteUInt16BigEndian(
            packet.AsSpan(stamp.At + VideoTiming.PacerExitOffset),
            VideoTiming.Delta(NtpDifference(stamp.Capture, NtpNow))
        );
    }

    // A sender report from the peer: one sample of the offset between the clocks.
    private void NoteSenderClock(ulong senderNtp, long arrivalMicros)
    {
        TimeSpan roundTrip = _iceAgent?.SelectedRoundTrip ?? ReportedRoundTripTime;
        long sample = (NtpDifference(senderNtp, NtpAt(arrivalMicros)) - (roundTrip / 2)).Ticks;
        lock (_clockGate)
        {
            _clockSamples.Enqueue(sample);
            while (_clockSamples.Count > ClockSamples)
            {
                _ = _clockSamples.Dequeue();
            }

            long[] sorted = [.. _clockSamples];
            Array.Sort(sorted);
            _clockOffsetTicks = sorted[sorted.Length / 2];
        }
    }
}
