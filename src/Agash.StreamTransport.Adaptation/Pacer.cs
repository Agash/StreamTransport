using System.Buffers;
using Agash.StreamTransport.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.Adaptation;

/// <summary>
/// Spreads outgoing packets over time at the congestion controller's pacing rate, so a keyframe does not
/// leave as one burst that overflows a bottleneck queue, and so retransmissions and FEC repair spend the
/// same budget as the media. One send loop sends what may go and otherwise sleeps on the clock until the
/// next packet may go or another arrives.
/// </summary>
/// <remarks>
/// Sends run one at a time in queue order, so the callback may keep per-packet state (an SRTP context)
/// without locking.
/// </remarks>
public sealed partial class Pacer : IAsyncDisposable
{
    private readonly Func<PacedPacket, CancellationToken, ValueTask> _send;
    private readonly TimeProvider _time;
    private readonly long _origin;
    private readonly ILogger _logger;
    private readonly PacingQueue _queue;
    private readonly Lock _gate = new();
    private readonly WakeSignal _wake;
    private readonly CancellationTokenSource _stop = new();
    private readonly long[] _sentBytes = new long[4];
    private readonly Task _loop;

    /// <summary>A pacer sending through a callback.</summary>
    /// <param name="send">Sends one packet; the pacer returns its buffer afterwards.</param>
    /// <param name="packetOverhead">
    /// Bytes each packet costs beyond its own length on the wire (for example UDP, IP and the SRTP tag),
    /// which the budget counts.
    /// </param>
    /// <param name="timeProvider">The clock pacing runs on; the system's when null.</param>
    /// <param name="logger">Where failed sends are logged.</param>
    public Pacer(
        Func<PacedPacket, CancellationToken, ValueTask> send,
        int packetOverhead = 0,
        TimeProvider? timeProvider = null,
        ILogger<Pacer>? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentOutOfRangeException.ThrowIfNegative(packetOverhead);
        _send = send;
        _time = timeProvider ?? TimeProvider.System;
        _origin = _time.GetTimestamp();
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _queue = new PacingQueue { PacketOverhead = packetOverhead };
        _wake = new WakeSignal(_time, () => Now);
        _loop = Task.Run(() => SendLoopAsync(_stop.Token));
    }

    /// <summary>The pacing rate in bits per second; zero sends without pacing.</summary>
    public long BitsPerSecond
    {
        get
        {
            lock (_gate)
            {
                return _queue.BitsPerSecond;
            }
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            lock (_gate)
            {
                _queue.BitsPerSecond = value;
            }

            _wake.Signal();
        }
    }

    /// <summary>
    /// Whether a packet other than audio may go now, given its cost in bytes: the congestion controller's
    /// send window. Packets wait while it says no; <see cref="Wake"/> when it may have opened.
    /// </summary>
    public Func<int, bool>? Gate
    {
        get => _queue.Gate;
        init => _queue.Gate = value;
    }

    /// <summary>How long the oldest packet waiting behind the rate or the window has waited.</summary>
    public TimeSpan QueueDelay
    {
        get
        {
            lock (_gate)
            {
                return _queue.QueueDelay(Now);
            }
        }
    }

    /// <summary>Looks at the queue again now, after something that may let a waiting packet go.</summary>
    public void Wake() => _wake.Signal();

    /// <summary>Queues a packet; the pacer owns its buffer from here.</summary>
    /// <param name="packet">The packet.</param>
    public void Enqueue(PacedPacket packet)
    {
        lock (_gate)
        {
            _queue.Enqueue(packet, Now);
        }

        _wake.Signal();
    }

    /// <summary>
    /// The bytes sent so far in a class, counting the per-packet overhead. Sampling two points gives the
    /// class's rate, from which the media side subtracts repair traffic from the encoder's target.
    /// </summary>
    /// <param name="trafficClass">The class.</param>
    /// <returns>The bytes.</returns>
    public long SentBytes(TrafficClass trafficClass) =>
        Interlocked.Read(ref _sentBytes[(int)trafficClass]);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

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
            _ = Interlocked.Add(
                ref _sentBytes[(int)packet.Class],
                packet.Length + _queue.PacketOverhead
            );
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One failed send (a transient socket error, a connection closing) drops that packet; the
            // transport's recovery repairs it, and the loop carries on.
            LogSendFailed(exception, packet.Class);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet.Buffer);
        }
    }

    [LoggerMessage(2600, LogLevel.Debug, "Sending a {TrafficClass} packet failed; it is dropped.")]
    private partial void LogSendFailed(Exception exception, TrafficClass trafficClass);
}
