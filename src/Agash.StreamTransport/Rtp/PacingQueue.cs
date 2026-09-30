namespace Agash.StreamTransport.Rtp;

/// <summary>What the pacer does next: send a packet, wait, or wait for packets.</summary>
/// <param name="Packet">The packet to send now, if one may go.</param>
/// <param name="Wait">How long until the next packet may go, when none may go now; null when the queue is empty.</param>
internal readonly record struct PacingStep(PacedPacket? Packet, TimeSpan? Wait);

/// <summary>
/// The pacing decisions, apart from threads and clocks: every call is given the time, so tests step it
/// directly. Audio goes first and never waits, and may drive the budget negative. Video waits for a token
/// bucket at the pacing rate; saved-up budget is capped at a short burst, and never below the packet
/// waiting. A standing video queue drains at whatever rate empties it within <see cref="MaxQueueDelay"/>.
/// </summary>
internal sealed class PacingQueue
{
    /// <summary>The longest a queued video packet waits before the queue drains faster than the rate.</summary>
    public static readonly TimeSpan MaxQueueDelay = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan MaxBurst = TimeSpan.FromMilliseconds(5);

    private readonly Queue<PacedPacket> _audio = new();
    private readonly Queue<(PacedPacket Packet, TimeSpan Enqueued)> _video = new();
    private long _videoQueuedBytes;
    private double _budgetBytes;
    private TimeSpan _lastRefill;

    /// <summary>The pacing rate; zero sends without pacing.</summary>
    public long BitsPerSecond { get; set; }

    /// <summary>Whether nothing is queued.</summary>
    public bool IsEmpty => _audio.Count == 0 && _video.Count == 0;

    public void EnqueueAudio(PacedPacket packet) => _audio.Enqueue(packet);

    public void EnqueueVideo(PacedPacket packet, TimeSpan now)
    {
        _video.Enqueue((packet, now));
        _videoQueuedBytes += packet.Length;
    }

    /// <summary>The next step at a time; a packet it returns is taken off the queue.</summary>
    /// <param name="now">The time on the pacer's clock.</param>
    /// <returns>The step.</returns>
    public PacingStep Next(TimeSpan now)
    {
        if (_audio.TryDequeue(out PacedPacket audio))
        {
            _budgetBytes -= audio.Length;
            return new PacingStep(audio, null);
        }

        if (!_video.TryPeek(out (PacedPacket Packet, TimeSpan Enqueued) video))
        {
            return new PacingStep(null, null);
        }

        TimeSpan wait = WaitFor(video.Packet.Length, video.Enqueued, now);
        if (wait > TimeSpan.Zero)
        {
            return new PacingStep(null, wait);
        }

        _ = _video.Dequeue();
        _videoQueuedBytes -= video.Packet.Length;
        _budgetBytes -= video.Packet.Length;
        return new PacingStep(video.Packet, null);
    }

    /// <summary>Takes every queued packet, for returning their buffers.</summary>
    /// <returns>The packets.</returns>
    public IEnumerable<PacedPacket> Clear()
    {
        while (_audio.TryDequeue(out PacedPacket audio))
        {
            yield return audio;
        }

        while (_video.TryDequeue(out (PacedPacket Packet, TimeSpan) video))
        {
            yield return video.Packet;
        }

        _videoQueuedBytes = 0;
    }

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

        double bytesPerSecond = Math.Max(rate / 8.0, _videoQueuedBytes / remaining.TotalSeconds);
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
