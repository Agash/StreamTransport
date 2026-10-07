namespace Agash.StreamTransport.Adaptation;

/// <summary>
/// Resolves what became of each sent packet from feedback, the same way for every transport. A packet
/// reported missing is lost only when it stays missing for the reordering window after a later packet
/// arrived; one that then arrives anyway was reordered, and the window grows to the delay it showed
/// (SCReAMv2, draft-ietf-ccwg-rfc8298bis-screamv2-01 section 4.1.2, after RACK in RFC 8985). A packet no
/// feedback mentions, such as the last one of a stream or one whose report was itself lost, is lost by
/// RACK's time rule (RFC 8985 section 6.2): once a packet sent after it was delivered, and a round trip and
/// the reordering window have passed since it was sent. A packet is resolved once, however many
/// overlapping reports cover it (RFC 8888 section 3.1).
/// </summary>
/// <remarks>Not thread-safe: the transport feeds it from one place.</remarks>
public sealed class DeliveryTracker
{
    // A large reordering seen once should not slow loss detection for ever: the window decays with this
    // time constant, and never exceeds the smoothed round trip.
    private static readonly TimeSpan WindowDecay = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _history;
    private readonly Dictionary<long, Entry> _packets = [];
    private readonly Queue<long> _order = new();
    private readonly List<long> _missing = [];

    // Packets in sending order that no feedback has mentioned yet, for RACK's time rule.
    private readonly Queue<long> _unreported = new();

    // RACK.xmit_ts and RACK.rtt: when the latest-sent delivered packet was sent, and how long from then until
    // the feedback saying so arrived, the receiver's feedback delay included.
    private TimeSpan _rackSentAt = TimeSpan.MinValue;
    private TimeSpan _rackRoundTrip;
    private long _highestDelivered = long.MinValue;
    private TimeSpan _decayedAt;
    private TimeSpan _smoothedRoundTrip;

    /// <summary>A tracker that remembers sent packets for a while.</summary>
    /// <param name="history">How long a sent packet waits for feedback; two seconds when null.</param>
    public DeliveryTracker(TimeSpan? history = null)
    {
        _history = history ?? TimeSpan.FromSeconds(2);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_history, TimeSpan.Zero);
    }

    /// <summary>How long a packet may stay missing behind a later one before it counts as lost.</summary>
    public TimeSpan ReorderingWindow { get; private set; }

    /// <summary>How many sent packets are remembered.</summary>
    public int Count => _packets.Count;

    /// <summary>Notes a packet as it goes on the wire.</summary>
    /// <param name="packet">The packet.</param>
    public void OnSent(in SentPacket packet)
    {
        _packets[packet.Id] = new Entry(packet, State.InFlight, default);
        _order.Enqueue(packet.Id);
        _unreported.Enqueue(packet.Id);
        while (
            _order.TryPeek(out long oldest)
            && (
                !_packets.TryGetValue(oldest, out Entry entry)
                || packet.SentAt - entry.Packet.SentAt > _history
            )
        )
        {
            _ = _order.Dequeue();
            _ = _packets.Remove(oldest);
        }
    }

    /// <summary>Resolves the packets one piece of feedback covers.</summary>
    /// <param name="reports">What the feedback says, packet by packet.</param>
    /// <param name="now">Now, on the sender's monotonic clock.</param>
    /// <param name="observations">Where newly resolved packets go.</param>
    /// <returns>
    /// A round-trip sample: from sending the latest-sent delivered packet the feedback covers until the
    /// feedback arrived.
    /// </returns>
    public TimeSpan? OnFeedback(
        ReadOnlySpan<PacketReport> reports,
        TimeSpan now,
        List<PacketObservation> observations
    )
    {
        ArgumentNullException.ThrowIfNull(observations);
        Decay(now);
        TimeSpan? roundTrip = null;
        TimeSpan latestSent = TimeSpan.MinValue;
        foreach (PacketReport report in reports)
        {
            if (!_packets.TryGetValue(report.Id, out Entry entry))
            {
                continue;
            }

            if (!report.Received)
            {
                if (entry.State == State.InFlight)
                {
                    _packets[report.Id] = entry with { State = State.Missing, Since = now };
                    _missing.Add(report.Id);
                }

                continue;
            }

            PacketOutcome outcome;
            switch (entry.State)
            {
                case State.Delivered:
                    continue;
                case State.Lost:
                    outcome = PacketOutcome.DeliveredLate;
                    ReorderingWindow = Max(ReorderingWindow, now - entry.Since);
                    break;
                default:
                    outcome = PacketOutcome.Delivered;
                    break;
            }

            _packets[report.Id] = entry with { State = State.Delivered };
            _highestDelivered = Math.Max(_highestDelivered, report.Id);
            if (entry.Packet.SentAt >= _rackSentAt)
            {
                _rackSentAt = entry.Packet.SentAt;
                _rackRoundTrip = now - entry.Packet.SentAt;
            }

            observations.Add(
                new PacketObservation(entry.Packet, outcome, report.ArrivedAt, report.Ecn)
            );

            // The round trip runs from sending the latest-sent packet the feedback covers until the
            // feedback arrives, the receiver's wait before reporting included: that is how long a packet
            // occupies a congestion window, which SCReAMv2's s_rtt measures (computed as RFC 6298 does, from
            // send to acknowledgement; the reference implementation takes the newest packet of each report).
            // Leaving the wait out sizes the window for a round trip shorter than its packets stay in it,
            // which on a fast path holds the sender far below its target.
            if (entry.Packet.SentAt > latestSent)
            {
                latestSent = entry.Packet.SentAt;
                roundTrip = now - entry.Packet.SentAt;
            }
        }

        if (roundTrip is { } rtt)
        {
            _smoothedRoundTrip =
                _smoothedRoundTrip == TimeSpan.Zero
                    ? rtt
                    : (_smoothedRoundTrip * 7 / 8) + (rtt / 8);
            if (ReorderingWindow > _smoothedRoundTrip)
            {
                ReorderingWindow = _smoothedRoundTrip;
            }
        }

        DetectLosses(now, observations);
        return roundTrip;
    }

    /// <summary>
    /// Forgets every packet and estimate for a new network path: what the old path delivered or lost must
    /// not count toward the new one's congestion control or round trip (RFC 9000 section 9.4), so feedback
    /// about those packets resolves nothing.
    /// </summary>
    public void OnPathChanged()
    {
        _packets.Clear();
        _order.Clear();
        _missing.Clear();
        _unreported.Clear();
        _rackSentAt = TimeSpan.MinValue;
        _rackRoundTrip = TimeSpan.Zero;
        _highestDelivered = long.MinValue;
        _smoothedRoundTrip = TimeSpan.Zero;
        ReorderingWindow = TimeSpan.Zero;
    }

    /// <summary>Declares lost the missing packets whose reordering window has passed, without new feedback.</summary>
    /// <param name="now">Now, on the sender's monotonic clock.</param>
    /// <param name="observations">Where newly lost packets go.</param>
    public void OnTick(TimeSpan now, List<PacketObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        DetectLosses(now, observations);
    }

    private void DetectLosses(TimeSpan now, List<PacketObservation> observations)
    {
        DetectUnreportedLosses(now, observations);

        int kept = 0;
        for (int i = 0; i < _missing.Count; i++)
        {
            long id = _missing[i];
            if (!_packets.TryGetValue(id, out Entry entry) || entry.State != State.Missing)
            {
                continue;
            }

            // Lost once a later packet arrived and the window has passed since this one was seen missing.
            if (id < _highestDelivered && now - entry.Since >= ReorderingWindow)
            {
                _packets[id] = entry with { State = State.Lost, Since = now };
                observations.Add(
                    new PacketObservation(
                        entry.Packet,
                        PacketOutcome.Lost,
                        null,
                        EcnCodepoint.NotEct
                    )
                );
                continue;
            }

            _missing[kept++] = id;
        }

        _missing.RemoveRange(kept, _missing.Count - kept);
    }

    // RFC 8985 section 6.2, step 5: a packet sent before the latest-sent delivered one is lost once
    // RACK.rtt and the reordering window have passed since it was sent. RACK's window is at least a quarter
    // of the round trip (section 6.2, step 4). Packets wait in sending order, so the first that is not
    // yet due ends the scan.
    private void DetectUnreportedLosses(TimeSpan now, List<PacketObservation> observations)
    {
        TimeSpan window = Max(ReorderingWindow, _smoothedRoundTrip / 4);
        while (_unreported.TryPeek(out long id))
        {
            if (!_packets.TryGetValue(id, out Entry entry) || entry.State != State.InFlight)
            {
                _ = _unreported.Dequeue();
                continue;
            }

            if (
                entry.Packet.SentAt > _rackSentAt
                || now < entry.Packet.SentAt + _rackRoundTrip + window
            )
            {
                return;
            }

            _ = _unreported.Dequeue();
            _packets[id] = entry with { State = State.Lost, Since = now };
            observations.Add(
                new PacketObservation(entry.Packet, PacketOutcome.Lost, null, EcnCodepoint.NotEct)
            );
        }
    }

    private void Decay(TimeSpan now)
    {
        if (ReorderingWindow > TimeSpan.Zero && now > _decayedAt)
        {
            ReorderingWindow *= Math.Exp(-((now - _decayedAt) / WindowDecay));
        }

        _decayedAt = now;
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private enum State : byte
    {
        InFlight,
        Missing,
        Lost,
        Delivered,
    }

    private readonly record struct Entry(SentPacket Packet, State State, TimeSpan Since);
}
