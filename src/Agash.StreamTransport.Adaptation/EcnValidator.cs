namespace Agash.StreamTransport.Adaptation;

/// <summary>Where a path's ECN support stands.</summary>
public enum EcnState
{
    /// <summary>ECN was not agreed with the peer: nothing is marked.</summary>
    Disabled,

    /// <summary>A fraction of packets is marked until feedback shows the marks arrive intact.</summary>
    Testing,

    /// <summary>The path carries the marks: every media packet is marked.</summary>
    Capable,

    /// <summary>
    /// The path loses, clears or rewrites marked packets: nothing is marked until a later retry.
    /// </summary>
    Failed,
}

/// <summary>
/// Validates ECN on a path before relying on it and keeps checking while using it (RFC 6679 sections 7.2
/// and 7.4, RFC 9000 section 13.4.2), so a middlebox that drops, clears or rewrites ECN fields cannot
/// blackhole the media or hide congestion. While testing it marks a fraction of the media; once feedback
/// shows enough marked packets arriving with their mark (or CE), it marks all of it. Marked packets that
/// arrive unmarked or re-marked, or that are lost while unmarked ones arrive, fail it; it retries after a
/// while, and from scratch when the path changes.
/// </summary>
/// <param name="retryInterval">How long after a failure it tries again; two minutes when null.</param>
/// <remarks>Not thread-safe: the transport serializes calls.</remarks>
public sealed class EcnValidator(TimeSpan? retryInterval = null)
{
    // While testing, one packet in this many is marked (RFC 6679 section 7.2.1: a small fraction, never all).
    private const int ProbeEvery = 4;

    // Marked packets that must arrive with their mark before the path counts as capable: more than three
    // (RFC 6679 section 7.2.1).
    private const int ValidationThreshold = 4;

    // Marked packets arriving cleared or rewritten, or lost while unmarked ones arrive, that fail the path.
    private const int FailureThreshold = 3;

    // Consecutive marked packets lost, with none arriving in between, that fail a path already capable.
    private const int CapableLossRun = 16;

    private readonly TimeSpan _retryInterval = retryInterval ?? TimeSpan.FromMinutes(2);
    private readonly Dictionary<long, EcnCodepoint> _marked = [];
    private readonly Queue<long> _markedOrder = new();
    private long _sent;
    private int _validated;
    private int _broken;
    private int _markedLost;
    private int _unmarkedDelivered;
    private TimeSpan _failedAt;

    /// <summary>Where the path's ECN support stands.</summary>
    public EcnState State { get; private set; } = EcnState.Disabled;

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    public event Action<EcnState>? StateChanged;

    /// <summary>Starts testing the path: ECN was agreed, or the path changed.</summary>
    public void Start() => Enter(EcnState.Testing);

    /// <summary>Stops marking: ECN is not agreed with the peer.</summary>
    public void Disable() => Enter(EcnState.Disabled);

    /// <summary>The codepoint a media packet carries now.</summary>
    /// <param name="packetId">The packet's id, as feedback will name it.</param>
    /// <param name="codepoint">The ECT codepoint the congestion controller's response suits.</param>
    /// <param name="now">Now, on the sender's monotonic clock.</param>
    /// <returns>The codepoint, or not-ECT.</returns>
    public EcnCodepoint MarkFor(long packetId, EcnCodepoint codepoint, TimeSpan now)
    {
        if (State == EcnState.Failed && now - _failedAt >= _retryInterval)
        {
            // RFC 6679 section 7.4.1: retry, in case the path now carries ECN.
            Enter(EcnState.Testing);
        }

        bool mark =
            codepoint is EcnCodepoint.Ect0 or EcnCodepoint.Ect1
            && (
                State == EcnState.Capable
                || (State == EcnState.Testing && _sent++ % ProbeEvery == 0)
            );
        if (!mark)
        {
            return EcnCodepoint.NotEct;
        }

        _marked[packetId] = codepoint;
        _markedOrder.Enqueue(packetId);
        while (_markedOrder.Count > 4096)
        {
            _ = _marked.Remove(_markedOrder.Dequeue());
        }

        return codepoint;
    }

    /// <summary>Checks what feedback says about the marks against what was sent.</summary>
    /// <param name="observations">The packets the feedback resolved.</param>
    /// <param name="now">Now, on the sender's monotonic clock.</param>
    public void OnFeedback(ReadOnlySpan<PacketObservation> observations, TimeSpan now)
    {
        if (State is EcnState.Disabled or EcnState.Failed)
        {
            return;
        }

        foreach (PacketObservation observation in observations)
        {
            bool marked = _marked.Remove(observation.Packet.Id, out EcnCodepoint sent);
            if (observation.Outcome == PacketOutcome.Lost)
            {
                _markedLost += marked ? 1 : 0;
                continue;
            }

            if (!marked)
            {
                _unmarkedDelivered++;
                continue;
            }

            // Arrived with its own mark or CE: carried. Arrived not-ECT or with the other ECT: cleared or
            // rewritten on the way.
            if (observation.Ecn == sent || observation.Ecn == EcnCodepoint.Ce)
            {
                _validated++;
                _markedLost = 0;
            }
            else
            {
                _broken++;
            }
        }

        // A blackhole: marked packets keep vanishing while unmarked ones get through, or, once everything is
        // marked, a run of losses with nothing arriving, which RFC 6679 section 7.4.1 answers by sending
        // not-ECT to see whether the marks are the cause.
        bool blackhole =
            (_markedLost >= FailureThreshold && _unmarkedDelivered >= FailureThreshold)
            || (State == EcnState.Capable && _markedLost >= CapableLossRun);
        if (_broken >= FailureThreshold || blackhole)
        {
            _failedAt = now;
            Enter(EcnState.Failed);
        }
        else if (State == EcnState.Testing && _validated >= ValidationThreshold)
        {
            Enter(EcnState.Capable);
        }
    }

    private void Enter(EcnState state)
    {
        _validated = 0;
        _broken = 0;
        _markedLost = 0;
        _unmarkedDelivered = 0;
        _sent = 0;
        _marked.Clear();
        _markedOrder.Clear();
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(state);
    }
}
