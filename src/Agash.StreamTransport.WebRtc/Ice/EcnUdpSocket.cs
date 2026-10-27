using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.Threading;

namespace Agash.StreamTransport.WebRtc.Ice;

/// <summary>
/// A real UDP <see cref="IIceSocket"/> that reads the per-packet ECN mark (IP TOS / IPv6 Traffic-Class low two
/// bits) so the congestion controller can react to L4S ECN-CE. The managed <see cref="Socket"/> receive APIs
/// discard the kernel's ancillary control data (<c>IPPacketInformation</c> exposes only the destination address
/// and interface - never the TOS/ECN byte), so reading ECN requires the native <c>recvmsg</c> (Unix) /
/// <c>WSARecvMsg</c> (Windows) path and manual cmsg parsing. That is done on a dedicated blocking receive
/// thread per socket; sends stay synchronous so the socket remains in blocking mode for the thread.
///
/// <para>Each datagram carries the codepoint its sender asks for, set per packet through the native send, so
/// media can be ECN-capable while STUN, DTLS and RTCP are not. Marking and ECN reception are best-effort:
/// where the OS refuses an option the socket still works, just without ECN (Ecn=0).</para>
///
/// <para>Per-platform struct layouts (msghdr/cmsghdr/sockaddr) and option numbers are verified by
/// <c>EcnLoopbackTests</c>, which marks a loopback datagram and asserts the value is read back - a wrong
/// layout fails that test on the affected OS rather than shipping. The real-socket ICE handshake tests
/// (loopback connect + data) additionally exercise the source-address parsing end to end.</para>
/// </summary>
internal sealed class EcnUdpSocket : IIceSocket
{
    private const int MaxDatagram = 2048;

    private readonly Socket _socket;
    private readonly bool _ipv6;
    private readonly Thread _recvThread;
    private const int Queued = 64;

    // The datagrams the receive thread has read and the reader has not, oldest first. When the reader
    // falls behind, the oldest is dropped: the receive thread never blocks, and a stale datagram is
    // expendable. Guarded by itself; the reader waits on _arrived, which allocates nothing per wait.
    private readonly Received[] _queue = new Received[Queued];
    private readonly WakeSignal _arrived = new();
    private int _head;
    private int _count;
    private bool _completed;
    private readonly WindowsQosFlows? _qos = OperatingSystem.IsWindows() ? new() : null;
    private volatile bool _stopping;

    public EcnUdpSocket(Socket socket)
    {
        _socket = socket;
        LocalEndPoint = (IPEndPoint)socket.LocalEndPoint!;
        _ipv6 = LocalEndPoint.AddressFamily == AddressFamily.InterNetworkV6;
        _socket.Blocking = true; // the receive thread blocks in native recvmsg/WSARecvMsg.

        EcnInterop.EnableEcnReceive(_socket, _ipv6);

        _recvThread = new Thread(ReceiveLoop)
        {
            IsBackground = true,
            Name = $"ice-recv {LocalEndPoint}",
        };
        _recvThread.Start();
    }

    public IPEndPoint LocalEndPoint { get; }

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> data,
        IPEndPoint destination,
        IpMarking marking,
        CancellationToken cancellationToken = default
    )
    {
        // Synchronous send keeps the socket in blocking mode (a managed async send would flip it non-blocking and
        // break the blocking native receive). UDP sendto does not block in practice for datagrams this size.
        try
        {
            if (
                OperatingSystem.IsWindows()
                && _qos is { } qos
                && marking.DiffServ != DiffServCodepoint.Default
            )
            {
                qos.Mark(_socket, destination, marking.DiffServ);
            }

            EcnInterop.Send(_socket, data.Span, destination, marking.Value);
        }
        catch (SocketException)
        {
            // A datagram to an unreachable/invalid candidate is expendable: ICE connectivity checks routinely
            // probe addresses that turn out to be unroutable. The managed socket discards these via a faulted
            // fire-and-forget task; surfacing them synchronously would tear down the check loop instead.
        }
        catch (ObjectDisposedException)
        {
            // Socket closed during shutdown; the receive side surfaces disposal to the reader.
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IceReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken
    ) =>
        TryTake(buffer.Span, out IceReceiveResult result)
            ? new ValueTask<IceReceiveResult>(result)
            : WaitAndReceiveAsync(buffer, cancellationToken);

    // Pooled, so a reader that waits for each datagram does not allocate a state machine every time.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<IceReceiveResult> WaitAndReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            _ = await _arrived.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (TryTake(buffer.Span, out IceReceiveResult result))
            {
                return result;
            }
        }
    }

    private bool TryTake(Span<byte> buffer, out IceReceiveResult result)
    {
        Received item;
        lock (_queue)
        {
            if (_count == 0)
            {
                result = default;
                return _completed ? throw new ObjectDisposedException(nameof(EcnUdpSocket)) : false;
            }

            item = _queue[_head];
            _queue[_head] = default;
            _head = (_head + 1) % Queued;
            _count--;
        }

        int len = Math.Min(item.Length, buffer.Length);
        item.Buffer.AsSpan(0, len).CopyTo(buffer);
        ArrayPool<byte>.Shared.Return(item.Buffer);
        result = new IceReceiveResult(len, item.RemoteEndPoint, item.TrafficClass);
        return true;
    }

    private void Enqueue(Received item)
    {
        byte[]? dropped = null;
        lock (_queue)
        {
            if (_completed)
            {
                dropped = item.Buffer;
            }
            else
            {
                if (_count == Queued)
                {
                    dropped = _queue[_head].Buffer;
                    _head = (_head + 1) % Queued;
                    _count--;
                }

                _queue[(_head + _count) % Queued] = item;
                _count++;
            }
        }

        if (dropped is not null)
        {
            ArrayPool<byte>.Shared.Return(dropped);
        }

        _arrived.Signal();
    }

    // No more datagrams come; the reader takes what is queued, then sees the socket closed.
    private void Complete()
    {
        lock (_queue)
        {
            _completed = true;
        }

        _arrived.Signal();
    }

    private void ReceiveLoop()
    {
        nint handle = _socket.Handle;
        EcnInterop.SenderCache senders = new();
        int consecutiveErrors = 0;
        while (!_stopping)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(MaxDatagram);
            int len;
            byte trafficClass;
            IPEndPoint? remote;
            try
            {
                len = EcnInterop.Receive(
                    handle,
                    rented,
                    _ipv6,
                    senders,
                    out trafficClass,
                    out remote
                );
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(rented);
                break; // unexpected interop failure: stop the loop, surface as a closed socket to the reader.
            }

            if (len < 0 || remote is null)
            {
                ArrayPool<byte>.Shared.Return(rented);
                if (_stopping)
                {
                    break;
                }

                if (++consecutiveErrors >= 16)
                {
                    break; // a persistent error (not just a transient ICMP) - give up rather than spin.
                }

                continue;
            }

            consecutiveErrors = 0;
            Enqueue(new Received(rented, len, remote, trafficClass));
        }

        Complete();
    }

    public void Dispose()
    {
        _stopping = true;
        _socket.Dispose(); // unblocks the native receive (returns an error), so the thread exits.
        if (OperatingSystem.IsWindows())
        {
            _qos?.Dispose();
        }
        // Queued datagrams go back to the pool; a reader sees the socket closed.
        lock (_queue)
        {
            _completed = true;
            for (; _count > 0; _count--, _head = (_head + 1) % Queued)
            {
                ArrayPool<byte>.Shared.Return(_queue[_head].Buffer);
                _queue[_head] = default;
            }
        }

        _arrived.Signal();
    }

    private readonly record struct Received(
        byte[] Buffer,
        int Length,
        IPEndPoint RemoteEndPoint,
        byte TrafficClass
    );
}
