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
/// leave as one burst that overflows a bottleneck queue. The decisions are the <see cref="PacingQueue"/>'s;
/// this runs them: one send loop that sends what may go, and otherwise sleeps on the injected clock
/// until the next packet may go or another arrives.
/// </summary>
internal sealed partial class RtpPacer : IAsyncDisposable
{
    private readonly Func<PacedPacket, CancellationToken, ValueTask> _send;
    private readonly TimeProvider _time;
    private readonly long _origin;
    private readonly ILogger _logger;
    private readonly PacingQueue _queue = new();
    private readonly Lock _gate = new();
    private readonly WakeSignal _wake;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

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
        _origin = timeProvider.GetTimestamp();
        _logger = logger ?? NullLogger.Instance;
        _queue.BitsPerSecond = bitsPerSecond;
        _wake = new WakeSignal(timeProvider, () => Now);
        _loop = Task.Run(() => SendLoopAsync(_stop.Token));
    }

    /// <summary>Changes the pacing rate; zero sends without pacing.</summary>
    /// <param name="bitsPerSecond">The new rate.</param>
    public void SetRate(long bitsPerSecond)
    {
        lock (_gate)
        {
            _queue.BitsPerSecond = bitsPerSecond;
        }

        _wake.Signal();
    }

    /// <summary>Queues an audio packet; it goes ahead of any video.</summary>
    /// <param name="packet">The packet, whose buffer the pacer now owns.</param>
    public void EnqueueAudio(PacedPacket packet)
    {
        lock (_gate)
        {
            _queue.EnqueueAudio(packet);
        }

        _wake.Signal();
    }

    /// <summary>Queues a video packet.</summary>
    /// <param name="packet">The packet, whose buffer the pacer now owns.</param>
    public void EnqueueVideo(PacedPacket packet)
    {
        lock (_gate)
        {
            _queue.EnqueueVideo(packet, Now);
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
            foreach (PacedPacket packet in _queue.Clear())
            {
                ArrayPool<byte>.Shared.Return(packet.Buffer);
            }
        }

        _stop.Dispose();
    }

    private TimeSpan Now => _time.GetElapsedTime(_origin);

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                PacingStep step;
                TimeSpan now;
                lock (_gate)
                {
                    now = Now;
                    step = _queue.Next(now);
                }

                if (step.Packet is { } packet)
                {
                    await SendAsync(packet, cancellationToken).ConfigureAwait(false);
                }
                else if (step.Wait is { } wait)
                {
                    _ = await _wake
                        .WaitUntilAsync(now + wait, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
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

    [LoggerMessage(
        2020,
        LogLevel.Debug,
        "Sending an RTP packet on SSRC {Ssrc} failed; it is dropped."
    )]
    private partial void LogSendFailed(Exception exception, uint ssrc);
}
