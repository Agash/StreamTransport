namespace Agash.StreamTransport.Adaptation;

/// <summary>
/// Resolves what became of each sent packet from feedback, the same way for every transport. A packet
/// reported missing is lost only when it stays missing for the reordering window after a later packet
/// arrived (SCReAMv2, draft-ietf-ccwg-rfc8298bis-screamv2-01 section 4.1.2, after RACK in RFC 8985). A
/// packet no feedback mentions once RACK's time rule would judge it (RFC 8985 section 6.2: a packet sent
/// after it was delivered, and a round trip and the reordering window passed since it was sent) is
/// <see cref="PacketOutcome.Unreported"/>, not lost: feedback reports contiguous ranges, so a packet
/// skipped behind a delivered one means its report was lost, as libwebrtc also reads it. A packet is
/// resolved once, however many overlapping reports cover it (RFC 8888 section 3.1).
/// </summary>
/// <remarks>
/// A packet declared lost that arrives anyway shows the window was too short, and it adapts as RACK's
/// does (RFC 8985 section 7.2): it grows to cover the delay that packet needed, and steps up by a quarter
/// of the minimum round trip, at most once a round trip. The steps hold for sixteen loss episodes after
/// the last such packet; then the window starts from a quarter round trip again. Delay that varies from
/// packet to packet (a cellular or Wi-Fi link) or feedback that overtakes earlier feedback both look like
/// this, and neither is congestion.
/// </remarks>
/// <remarks>Not thread-safe: the transport feeds it from one place.</remarks>
public sealed class DeliveryTracker
{
    // RFC 8985 section 7.2: the loss episodes a widened window lasts after the last spurious loss.
    private const int StepsPersist = 16;

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
    private TimeSpan _minRoundTrip = TimeSpan.MaxValue;

    // RACK's reo_wnd_mult less one, its reo_wnd_persist, and when the multiplier last grew.
    private int _steps;
    private int _stepsPersist;
    private TimeSpan _steppedAt = TimeSpan.MinValue;

    // When the latest loss was declared, so a loss a round trip after the last starts a new episode.
    private TimeSpan _lostAt = TimeSpan.MinValue;
    private TimeSpan _smoothedRoundTrip;

    /// <summary>A tracker that remembers sent packets for a while.</summary>
    /// <param name="history">How long a sent packet waits for feedback; two seconds when null.</param>
    public DeliveryTracker(TimeSpan? history = null)
    {
        _history = history ?? TimeSpan.FromSeconds(2);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_history, TimeSpan.Zero);
    }

    /// <summary>
    /// The longest a packet that was declared lost then took to arrive, beyond the allowance it was judged
    /// with: the reordering this path has shown.
    /// </summary>
    public TimeSpan ReorderingWindow { get; private set; }

    /// <summary>
    /// The allowance a packet gets beyond its round trip before it counts as lost: the reordering shown, or
    /// RACK's quarter of the minimum round trip times its multiplier, at most the smoothed round trip.
    /// </summary>
    public TimeSpan LossAllowance => Allowance(withFloor: true);

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
                if (entry.State is State.InFlight or State.Unreported)
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
                    Widen(entry, now);
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
            if (rtt < _minRoundTrip)
            {
                _minRoundTrip = rtt;
            }

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
        _minRoundTrip = TimeSpan.MaxValue;
        ReorderingWindow = TimeSpan.Zero;
        _steps = 0;
        _stepsPersist = 0;
        _steppedAt = TimeSpan.MinValue;
        _lostAt = TimeSpan.MinValue;
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
        DetectUnreported(now, observations);

        int kept = 0;
        for (int i = 0; i < _missing.Count; i++)
        {
            long id = _missing[i];
            if (!_packets.TryGetValue(id, out Entry entry) || entry.State != State.Missing)
            {
                continue;
            }

            // Lost once a later packet arrived and the window has passed since this one was seen missing. A
            // report saying so is direct, so until reordering shows the floor does not apply.
            TimeSpan allowance = Allowance(withFloor: _steps > 0);
            if (id < _highestDelivered && now - entry.Since >= allowance)
            {
                NoteLoss(now);
                _packets[id] = entry with
                {
                    State = State.Lost,
                    Since = now,
                    Window = now - entry.Since,
                };
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

    // RFC 8985 section 6.2, step 5: a packet sent before the latest-sent delivered one is due once RACK.rtt
    // and the reordering window have passed since it was sent. RACK's window is at least a quarter of the
    // round trip (section 6.2, step 4). Packets wait in sending order, so the first that is not yet due
    // ends the scan. One no feedback mentioned by then is unreported.
    private void DetectUnreported(TimeSpan now, List<PacketObservation> observations)
    {
        TimeSpan window = Allowance(withFloor: true);
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
            _packets[id] = entry with { State = State.Unreported, Since = now };
            observations.Add(
                new PacketObservation(
                    entry.Packet,
                    PacketOutcome.Unreported,
                    null,
                    EcnCodepoint.NotEct
                )
            );
        }
    }

    // The allowance for reordering: what this path has shown, and with the floor RACK's quarter of the minimum
    // round trip for each step, never beyond the smoothed round trip.
    private TimeSpan Allowance(bool withFloor)
    {
        TimeSpan allowance = ReorderingWindow;
        if (withFloor)
        {
            TimeSpan quarter =
                _minRoundTrip != TimeSpan.MaxValue ? _minRoundTrip / 4 : _smoothedRoundTrip / 4;
            allowance = Max(allowance, quarter * (1 + _steps));
        }

        return _smoothedRoundTrip > TimeSpan.Zero && allowance > _smoothedRoundTrip
            ? _smoothedRoundTrip
            : allowance;
    }

    // RFC 8985 section 7.2, for a packet declared lost that arrived after all: the window covers what it
    // needed, the allowance it was judged by plus how late it came, and the multiplier steps up once a round
    // trip, held for the next sixteen loss episodes.
    private void Widen(Entry entry, TimeSpan now)
    {
        ReorderingWindow = Max(ReorderingWindow, entry.Window + (now - entry.Since));
        if (_smoothedRoundTrip > TimeSpan.Zero && ReorderingWindow > _smoothedRoundTrip)
        {
            ReorderingWindow = _smoothedRoundTrip;
        }

        if (_steppedAt == TimeSpan.MinValue || now - _steppedAt >= _smoothedRoundTrip)
        {
            _steps++;
            _steppedAt = now;
        }

        _stepsPersist = StepsPersist;
    }

    // A loss a round trip or more after the last starts an episode; once enough pass with no packet coming
    // back late, the window starts over.
    private void NoteLoss(TimeSpan now)
    {
        if (_lostAt == TimeSpan.MinValue || now - _lostAt >= _smoothedRoundTrip)
        {
            if (_stepsPersist > 0 && --_stepsPersist == 0)
            {
                _steps = 0;
                ReorderingWindow = TimeSpan.Zero;
            }
        }

        _lostAt = now;
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private enum State : byte
    {
        InFlight,
        Missing,
        Lost,
        Unreported,
        Delivered,
    }

    // Since is when the packet was seen missing, or when it was declared lost; Window is the reordering
    // allowance a lost packet was judged with.
    private readonly record struct Entry(
        SentPacket Packet,
        State State,
        TimeSpan Since,
        TimeSpan Window = default
    );
}
