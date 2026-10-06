namespace Agash.StreamTransport.WebRtc.Rtcp;

/// <summary>
/// What one received RTP stream contributes to RFC 8888 congestion-control feedback. Each report covers
/// the sequence numbers since the previous one; a packet that arrives after a report covered it moves the
/// next report's start back, so the gap is reported again as received (section 3.1: a packet once reported
/// received stays received in later overlapping reports). The latest packets are kept for that.
/// </summary>
/// <remarks>The receive-side shape matches libwebrtc's, so reports read the same to a browser.</remarks>
internal sealed class CcfbReceiveTracker
{
    // A report block covers at most a quarter of the sequence space (section 3.1).
    private const int MaxPacketsPerSsrc = 16384;

    // How far back a late packet can still move a report's start.
    private const int KeepForReordering = 64;

    private readonly List<Entry> _packets = [];
    private long _first;
    private long _nextInFeedback;
    private long _highest = -1;
    private int _ignoredSinceFeedback;

    /// <summary>Records a packet's arrival.</summary>
    /// <param name="sequence">Its sequence number.</param>
    /// <param name="arrivalMicros">When it arrived, on the receiver's monotonic clock.</param>
    /// <param name="ecn">The ECN codepoint it arrived with.</param>
    public void OnPacket(ushort sequence, long arrivalMicros, byte ecn)
    {
        long extended = Unwrap(sequence);
        if (_packets.Count == 0)
        {
            // The first packet, or a restart: keep room behind it for reordered predecessors.
            _first = extended - KeepForReordering + 1;
            _nextInFeedback = extended;
            for (int i = 0; i < KeepForReordering; i++)
            {
                _packets.Add(default);
            }
        }

        if (extended < _first)
        {
            _ignoredSinceFeedback++;
            return;
        }

        long index = extended - _first;
        if (index >= _packets.Count)
        {
            if (index + 1 > MaxPacketsPerSsrc)
            {
                _ignoredSinceFeedback++;
                return;
            }

            while (_packets.Count <= index)
            {
                _packets.Add(default);
            }
        }

        Entry entry = _packets[(int)index];
        if (entry.Received)
        {
            // A duplicate: the first copy's arrival time stands, but any copy's CE mark is reported.
            if (ecn != Ecn.Ce || entry.Ecn == Ecn.Ce)
            {
                return;
            }

            entry = entry with { Ecn = Ecn.Ce };
        }
        else
        {
            entry = entry with { Received = true, ArrivalMicros = arrivalMicros, Ecn = ecn };
        }

        _packets[(int)index] = entry;

        // New information about a packet an earlier report covered: report from it again.
        _nextInFeedback = Math.Min(_nextInFeedback, extended);
    }

    /// <summary>
    /// Adds this stream's report blocks for a report at a time, and starts the next interval. Long runs are
    /// split into blocks of at most <paramref name="maxRun"/> packets.
    /// </summary>
    /// <param name="ssrc">The stream's SSRC.</param>
    /// <param name="nowMicros">The report's time, on the receiver's monotonic clock.</param>
    /// <param name="maxRun">The most packets one block reports.</param>
    /// <param name="reports">Where the blocks go.</param>
    public void AddToFeedback(uint ssrc, long nowMicros, int maxRun, List<CcfbStreamReport> reports)
    {
        if (_packets.Count == 0)
        {
            return;
        }

        long end = _first + _packets.Count;
        if (_nextInFeedback == end)
        {
            // Nothing new. Packets that arrived but all fell outside the window mean the sender's sequence
            // numbers restarted: start over with the next packet.
            if (_ignoredSinceFeedback > 0)
            {
                _packets.Clear();
                _highest = -1;
                _ignoredSinceFeedback = 0;
            }

            return;
        }

        _ignoredSinceFeedback = 0;
        for (long begin = _nextInFeedback; begin < end; begin += maxRun)
        {
            int run = (int)Math.Min(maxRun, end - begin);
            var metrics = new CcfbMetric[run];
            for (int i = 0; i < run; i++)
            {
                Entry entry = _packets[(int)(begin + i - _first)];
                metrics[i] = entry.Received
                    ? new CcfbMetric(
                        true,
                        entry.Ecn,
                        Ccfb.ArrivalTimeOffset(nowMicros - entry.ArrivalMicros)
                    )
                    : new CcfbMetric(false, 0, 0);
            }

            reports.Add(new CcfbStreamReport(ssrc, (ushort)begin, metrics));
        }

        _nextInFeedback = end;

        // Keep only the latest packets, which a late arrival can still reach back to.
        int drop = _packets.Count - KeepForReordering;
        if (drop > 0)
        {
            _packets.RemoveRange(0, drop);
            _first += drop;
        }
    }

    // RTP sequence numbers extended to 64 bits, assuming steps of less than half the space.
    private long Unwrap(ushort sequence)
    {
        if (_highest < 0)
        {
            _highest = sequence;
            return sequence;
        }

        long extended = _highest + (short)(ushort)(sequence - (ushort)_highest);
        _highest = Math.Max(_highest, extended);
        return extended;
    }

    private readonly record struct Entry(bool Received, long ArrivalMicros, byte Ecn);

    private static class Ecn
    {
        public const byte Ce = 0x03;
    }
}
