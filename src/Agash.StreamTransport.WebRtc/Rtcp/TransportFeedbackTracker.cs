namespace Agash.StreamTransport.WebRtc.Rtcp;

/// <summary>
/// What the receiver has to report in transport feedback: every packet's transport-wide sequence number
/// and arrival since the previous report. A packet that arrives after a report covered it moves the next
/// report's start back, so it is reported again as received, as the CCFB tracker does.
/// </summary>
/// <remarks>
/// Arrivals live in a ring indexed by sequence number, so recording one allocates nothing. Not
/// thread-safe: the connection holds its congestion lock around it.
/// </remarks>
internal sealed class TransportFeedbackTracker
{
    // How far back a late packet can still move a report's start.
    private const int KeepForReordering = 64;

    // The ring: the most packets one report can reach back over.
    private const int Capacity = 1 << 14;
    private const int Mask = Capacity - 1;

    private readonly long[] _sequenceAt = new long[Capacity];
    private readonly long[] _arrivalAt = new long[Capacity];
    private long _highest = -1;
    private long _nextInFeedback = -1;

    public TransportFeedbackTracker() => Array.Fill(_sequenceAt, -1);

    /// <summary>Whether anything arrived that a report has not covered.</summary>
    public bool HasNew => _highest >= 0 && _nextInFeedback <= _highest;

    /// <summary>Records a packet's arrival.</summary>
    /// <param name="sequence">Its transport-wide sequence number.</param>
    /// <param name="arrivalMicros">When it arrived, on the receiver's monotonic clock.</param>
    public void OnPacket(ushort sequence, long arrivalMicros)
    {
        long extended = Unwrap(sequence);
        if (_nextInFeedback < 0)
        {
            _nextInFeedback = extended;
        }
        else if (extended < _nextInFeedback - KeepForReordering || extended <= _highest - Mask)
        {
            return;
        }

        int slot = (int)(extended & Mask);
        if (_sequenceAt[slot] == extended)
        {
            return; // A duplicate: the first copy's arrival stands.
        }

        _sequenceAt[slot] = extended;
        _arrivalAt[slot] = arrivalMicros;
        _highest = Math.Max(_highest, extended);
        _nextInFeedback = Math.Min(_nextInFeedback, extended);
    }

    /// <summary>
    /// Takes what the next report covers, from the oldest packet not yet reported to the newest, and
    /// starts the next interval.
    /// </summary>
    /// <param name="arrivals">Where the packets go, consecutive from the first reported one.</param>
    public void TakeReport(List<TransportPacketArrival> arrivals)
    {
        ArgumentNullException.ThrowIfNull(arrivals);
        if (!HasNew)
        {
            return;
        }

        // A report starts at a received packet, which sets its reference time, and reaches back no
        // further than the ring holds.
        long start = Math.Max(_nextInFeedback, _highest - Mask + 1);
        while (start <= _highest && !Received(start))
        {
            start++;
        }

        for (long sequence = start; sequence <= _highest; sequence++)
        {
            arrivals.Add(
                new TransportPacketArrival(
                    (ushort)sequence,
                    Received(sequence) ? _arrivalAt[sequence & Mask] : null
                )
            );
        }

        _nextInFeedback = _highest + 1;
    }

    private bool Received(long sequence) => _sequenceAt[sequence & Mask] == sequence;

    // The 16-bit number extended against the highest seen, so wrapping keeps counting up.
    private long Unwrap(ushort sequence)
    {
        if (_highest < 0)
        {
            return sequence;
        }

        long candidate = (_highest & ~0xFFFFL) | sequence;
        if (candidate < _highest - 0x8000)
        {
            candidate += 0x10000;
        }
        else if (candidate > _highest + 0x8000)
        {
            candidate -= 0x10000;
        }

        return candidate;
    }
}
