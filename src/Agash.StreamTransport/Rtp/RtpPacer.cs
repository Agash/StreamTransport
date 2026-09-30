using System.Buffers;
using Agash.StreamTransport.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.Rtp;

/// <summary>One RTP packet waiting to go out; its buffer is rented and returned once it is sent.</summary>
/// <param name="PayloadType">The RTP payload type.</param>
/// <param name="Ssrc">The stream's SSRC.</param>
/// <param name="Timestamp">The RTP timestamp.</param>
/// <param name="Marker">The marker bit.</param>
/// <param name="Buffer">The payload, in a buffer rented from <see cref="ArrayPool{T}.Shared"/>.</param>
/// <param name="Length">The payload's length.</param>
/// <param name="CaptureNtp">The abs-capture-time to send, or zero for none.</param>
internal readonly record struct PacedPacket(
    byte PayloadType,
    uint Ssrc,
    uint Timestamp,
    bool Marker,
    byte[] Buffer,
    int Length,
    ulong CaptureNtp
);

/// <summary>
/// Spreads outgoing RTP over time at the congestion controller's pacing rate, so a keyframe does not
/// leave as one burst that overflows a bottleneck queue. Audio goes first and never waits; video waits
/// for budget, exactly as long as its deficit needs, and a packet arriving meanwhile ends the wait. A
/// standing video queue raises the drain rate so no packet waits longer than <see cref="MaxQueueDelay"/>.
/// One send loop, woken by packets, sleeping on the injected clock.
/// </summary>
internal sealed partial class RtpPacer : IAsyncDisposable
{
    /// <summary>The longest a queued video packet waits before the pacer drains faster than the rate.</summary>
    public static readonly TimeSpan MaxQueueDelay = TimeSpan.FromMilliseconds(250);

    // Budget left over from idle time is capped, so a quiet moment does not become a burst.
    private static readonly TimeSpan MaxBurst = TimeSpan.FromMilliseconds(5);

    private readonly Func<PacedPacket, CancellationToken, ValueTask> _send;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Queue<PacedPacket> _audio = new();
    private readonly Queue<(PacedPacket Packet, long Enqueued)> _video = new();
    private readonly Lock _gate = new();
    private readonly WakeSignal _wake;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private long _bitsPerSecond;
    private long _videoQueuedBytes;
    private double _budgetBytes;
    private long _lastRefill;

    /// <summary>A pacer sending through a callback.</summary>
    /// <param name="send">Sends one packet.</param>
    /// <param name="bitsPerSecond">The initial pacing rate; zero sends without pacing.</param>
    /// <param name="timeProvider">The clock pacing runs on.</param>
    /// <param name="logger">The logger.</param>
    public RtpPacer(
        Func<PacedPacket, CancellationToken, ValueTask> send,
        long bitsPerSecond,
        TimeProvider timeProvider,
        ILogger? logger = null
    )
    {
        _send = send;
        _time = timeProvider;
        _logger = logger ?? NullLogger.Instance;
        _bitsPerSecond = bitsPerSecond;
        _lastRefill = _time.GetTimestamp();
        _wake = new WakeSignal(timeProvider);
        _loop = Task.Run(() => SendLoopAsync(_stop.Token));
    }

    /// <summary>Changes the pacing rate; zero sends without pacing.</summary>
    /// <param name="bitsPerSecond">The new rate.</param>
    public void SetRate(long bitsPerSecond) =>
        Interlocked.Exchange(ref _bitsPerSecond, bitsPerSecond);

    /// <summary>Queues an audio packet; it goes ahead of any video.</summary>
    /// <param name="packet">The packet, whose buffer the pacer now owns.</param>
    public void EnqueueAudio(PacedPacket packet)
    {
        lock (_gate)
        {
            _audio.Enqueue(packet);
        }

        _wake.Signal();
    }

    /// <summary>Queues a video packet.</summary>
    /// <param name="packet">The packet, whose buffer the pacer now owns.</param>
    public void EnqueueVideo(PacedPacket packet)
    {
        lock (_gate)
        {
            _video.Enqueue((packet, _time.GetTimestamp()));
            _videoQueuedBytes += packet.Length;
        }

        _wake.Signal();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        lock (_gate)
        {
            while (_audio.TryDequeue(out PacedPacket packet))
            {
                ArrayPool<byte>.Shared.Return(packet.Buffer);
            }

            while (_video.TryDequeue(out (PacedPacket Packet, long) entry))
            {
                ArrayPool<byte>.Shared.Return(entry.Packet.Buffer);
            }
        }

        _stop.Dispose();
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                if (TryTakeAudio(out PacedPacket audio))
                {
                    Spend(audio.Length);
                    await SendAsync(audio, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!TryPeekVideo(out PacedPacket video, out long enqueued))
                {
                    await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                TimeSpan wait = WaitFor(video.Length, enqueued);
                if (wait > TimeSpan.Zero)
                {
                    _ = await _wake.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                TakeVideo();
                Spend(video.Length);
                await SendAsync(video, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Deliberately not logged: cancellation is how the pacer stops.
        }
    }

    private async ValueTask SendAsync(PacedPacket packet, CancellationToken cancellationToken)
    {
        try
        {
            await _send(packet, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One failed send (a transient socket error, a connection closing) drops that packet; NACK and
            // RTX or the next keyframe repair it, and the loop carries on.
            LogSendFailed(exception, packet.Ssrc);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet.Buffer);
        }
    }

    // How long to wait before a video packet of this size may go: zero when the budget covers it.
    private TimeSpan WaitFor(int bytes, long enqueued)
    {
        long rate = Interlocked.Read(ref _bitsPerSecond);
        if (rate <= 0)
        {
            return TimeSpan.Zero;
        }

        long now = _time.GetTimestamp();
        lock (_gate)
        {
            // A standing queue drains at whatever rate empties it within the queue delay limit.
            TimeSpan waited = _time.GetElapsedTime(enqueued, now);
            TimeSpan remaining = MaxQueueDelay - waited;
            double drain =
                remaining > TimeSpan.Zero
                    ? _videoQueuedBytes * 8 / remaining.TotalSeconds
                    : double.PositiveInfinity;
            double bytesPerSecond = Math.Max(rate, drain) / 8;
            if (double.IsPositiveInfinity(bytesPerSecond))
            {
                return TimeSpan.Zero;
            }

            Refill(now, bytesPerSecond, bytes);
            double deficit = bytes - _budgetBytes;
            return deficit <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(deficit / bytesPerSecond);
        }
    }

    // The cap on saved-up budget is a short burst at the rate, and never less than the packet waiting,
    // which could otherwise never be covered at a low rate.
    private void Refill(long now, double bytesPerSecond, int waiting)
    {
        double elapsed = _time.GetElapsedTime(_lastRefill, now).TotalSeconds;
        _lastRefill = now;
        _budgetBytes = Math.Min(
            _budgetBytes + (elapsed * bytesPerSecond),
            Math.Max(bytesPerSecond * MaxBurst.TotalSeconds, waiting)
        );
    }

    // Audio may drive the budget negative: it is small, never waits, and video makes up for it.
    private void Spend(int bytes)
    {
        lock (_gate)
        {
            _budgetBytes -= bytes;
        }
    }

    private bool TryTakeAudio(out PacedPacket packet)
    {
        lock (_gate)
        {
            return _audio.TryDequeue(out packet);
        }
    }

    private bool TryPeekVideo(out PacedPacket packet, out long enqueued)
    {
        lock (_gate)
        {
            if (_video.TryPeek(out (PacedPacket Packet, long Enqueued) entry))
            {
                (packet, enqueued) = entry;
                return true;
            }
        }

        packet = default;
        enqueued = 0;
        return false;
    }

    private void TakeVideo()
    {
        lock (_gate)
        {
            (PacedPacket packet, _) = _video.Dequeue();
            _videoQueuedBytes -= packet.Length;
        }
    }

    [LoggerMessage(
        2020,
        LogLevel.Debug,
        "Sending an RTP packet on SSRC {Ssrc} failed; it is dropped."
    )]
    private partial void LogSendFailed(Exception exception, uint ssrc);
}
