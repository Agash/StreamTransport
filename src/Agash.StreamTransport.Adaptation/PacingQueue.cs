namespace Agash.StreamTransport.Adaptation;

/// <summary>What the pacer does next: send a packet, wait, or wait for packets.</summary>
/// <param name="Packet">The packet to send now, if one may go.</param>
/// <param name="Wait">How long until the next packet may go, when none may go now; null when the queue is empty.</param>
internal readonly record struct PacingStep(PacedPacket? Packet, TimeSpan? Wait);

/// <summary>
/// The pacing decisions, apart from threads and clocks: every call is given the time, so tests step it
/// directly. Audio goes first and never waits, and may drive the budget negative. The other classes wait
/// for one token bucket at the pacing rate, retransmissions first, then video, then repair; saved-up
/// budget is capped at a short burst, and never below the packet waiting. A standing queue drains at
/// whatever rate empties it within <see cref="MaxQueueDelay"/>.
/// </summary>
internal sealed class PacingQueue
{
    /// <summary>The longest a queued packet waits before the queue drains faster than the rate.</summary>
    public static readonly TimeSpan MaxQueueDelay = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan MaxBurst = TimeSpan.FromMilliseconds(5);

    // While the send window is closed the queue looks again this often, besides being woken by feedback.
    private static readonly TimeSpan GateRecheck = TimeSpan.FromMilliseconds(10);

    private readonly Queue<PacedPacket> _audio = new();

    // Retransmission, video and repair, in the order they go.
    private readonly Queue<(PacedPacket Packet, TimeSpan Enqueued)>[] _paced =
    [
        new(),
        new(),
        new(),
    ];
    private long _pacedBytes;
    private double _budgetBytes;
    private TimeSpan _lastRefill;

    /// <summary>The pacing rate; zero sends without pacing.</summary>
    public long BitsPerSecond { get; set; }

    /// <summary>Bytes each packet costs beyond its own length on the wire (headers, authentication tags).</summary>
    public int PacketOverhead { get; init; }

    /// <summary>Whether a packet other than audio may go now (the congestion controller's send window).</summary>
    public Func<int, bool>? Gate { get; set; }

    /// <summary>How long the oldest packet waiting behind the rate or the window has waited.</summary>
    /// <param name="now">The time on the pacer's clock.</param>
    /// <returns>The wait, or zero when none waits.</returns>
    public TimeSpan QueueDelay(TimeSpan now)
    {
        TimeSpan oldest = now;
        foreach (Queue<(PacedPacket Packet, TimeSpan Enqueued)> queue in _paced)
        {
            if (
                queue.TryPeek(out (PacedPacket Packet, TimeSpan Enqueued) head)
                && head.Enqueued < oldest
            )
            {
                oldest = head.Enqueued;
            }
        }

        return now - oldest;
    }

    public void Enqueue(PacedPacket packet, TimeSpan now)
    {
        if (packet.Class == TrafficClass.Audio)
        {
            _audio.Enqueue(packet);
            return;
        }

        _paced[(int)packet.Class - 1].Enqueue((packet, now));
        _pacedBytes += Cost(packet);
    }

    /// <summary>The next step at a time; a packet it returns is taken off the queue.</summary>
    /// <param name="now">The time on the pacer's clock.</param>
    /// <returns>The step.</returns>
    public PacingStep Next(TimeSpan now)
    {
        if (_audio.TryDequeue(out PacedPacket audio))
        {
            _budgetBytes -= Cost(audio);
            return new PacingStep(audio, null);
        }

        foreach (Queue<(PacedPacket Packet, TimeSpan Enqueued)> queue in _paced)
        {
            if (!queue.TryPeek(out (PacedPacket Packet, TimeSpan Enqueued) next))
            {
                continue;
            }

            int cost = Cost(next.Packet);
            if (Gate is { } gate && !gate(cost))
            {
                return new PacingStep(null, GateRecheck);
            }

            TimeSpan wait = WaitFor(cost, next.Enqueued, now);
            if (wait > TimeSpan.Zero)
            {
                return new PacingStep(null, wait);
            }

            _ = queue.Dequeue();
            _pacedBytes -= cost;
            _budgetBytes -= cost;
            return new PacingStep(next.Packet, null);
        }

        return new PacingStep(null, null);
    }

    /// <summary>Takes every queued packet, for returning their buffers.</summary>
    /// <returns>The packets.</returns>
    public IEnumerable<PacedPacket> Clear()
    {
        while (_audio.TryDequeue(out PacedPacket audio))
        {
            yield return audio;
        }

        foreach (Queue<(PacedPacket Packet, TimeSpan Enqueued)> queue in _paced)
        {
            while (queue.TryDequeue(out (PacedPacket Packet, TimeSpan) next))
            {
                yield return next.Packet;
            }
        }

        _pacedBytes = 0;
    }

    private int Cost(PacedPacket packet) => packet.Length + PacketOverhead;

    private TimeSpan WaitFor(int bytes, TimeSpan enqueued, TimeSpan now)
    {
        long rate = BitsPerSecond;
        if (rate <= 0)
        {
            return TimeSpan.Zero;
        }

        TimeSpan remaining = MaxQueueDelay - (now - enqueued);
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        double bytesPerSecond = Math.Max(rate / 8.0, _pacedBytes / remaining.TotalSeconds);
        double elapsed = (now - _lastRefill).TotalSeconds;
        _lastRefill = now;
        _budgetBytes = Math.Min(
            _budgetBytes + (Math.Max(elapsed, 0) * bytesPerSecond),
            Math.Max(bytesPerSecond * MaxBurst.TotalSeconds, bytes)
        );
        double deficit = bytes - _budgetBytes;
        return deficit <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(deficit / bytesPerSecond);
    }
}
