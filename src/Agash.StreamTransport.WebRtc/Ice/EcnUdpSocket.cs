using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Agash.StreamTransport.WebRtc.Ice;

/// <summary>
/// A real UDP <see cref="IIceSocket"/> that reads the per-packet ECN mark (IP TOS / IPv6 Traffic-Class low two
/// bits) so the congestion controller can react to L4S ECN-CE. The managed <see cref="Socket"/> receive APIs
/// discard the kernel's ancillary control data (<c>IPPacketInformation</c> exposes only the destination address
/// and interface - never the TOS/ECN byte), so reading ECN requires the native <c>recvmsg</c> (Unix) /
/// <c>WSARecvMsg</c> (Windows) path and manual cmsg parsing. That is done on a dedicated blocking receive
/// thread per socket; sends stay synchronous so the socket remains in blocking mode for the thread.
///
/// <para>Outgoing packets are best-effort marked <c>ECT(1)</c> (L4S-capable transport) so a conformant
/// bottleneck will flip them to CE under load rather than dropping them. Marking and ECN reception are
/// best-effort: where the OS refuses an option the socket still works, just without ECN feedback (Ecn=0).</para>
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
    private readonly Channel<Received> _channel;
    private volatile bool _stopping;

    public EcnUdpSocket(Socket socket)
    {
        _socket = socket;
        LocalEndPoint = (IPEndPoint)socket.LocalEndPoint!;
        _ipv6 = LocalEndPoint.AddressFamily == AddressFamily.InterNetworkV6;
        _socket.Blocking = true; // the receive thread blocks in native recvmsg/WSARecvMsg.

        EcnInterop.EnableEct1OnSend(_socket, _ipv6);
        EcnInterop.EnableEcnReceive(_socket, _ipv6);

        _channel = Channel.CreateBounded<Received>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest, // never block the receive thread; stale datagrams are expendable.
        });

        _recvThread = new Thread(ReceiveLoop) { IsBackground = true, Name = $"ice-recv {LocalEndPoint}" };
        _recvThread.Start();
    }

    public IPEndPoint LocalEndPoint { get; }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, IPEndPoint destination, CancellationToken cancellationToken = default)
    {
        // Synchronous send keeps the socket in blocking mode (a managed async send would flip it non-blocking and
        // break the blocking native receive). UDP sendto does not block in practice for datagrams this size.
        try
        {
            _socket.SendTo(data.Span, SocketFlags.None, destination);
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

    public async ValueTask<IceReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        Received item;
        try
        {
            item = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(nameof(EcnUdpSocket));
        }

        int len = Math.Min(item.Length, buffer.Length);
        item.Buffer.AsSpan(0, len).CopyTo(buffer.Span);
        ArrayPool<byte>.Shared.Return(item.Buffer);
        return new IceReceiveResult(len, item.RemoteEndPoint, item.Ecn);
    }

    private void ReceiveLoop()
    {
        nint handle = _socket.Handle;
        int consecutiveErrors = 0;
        while (!_stopping)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(MaxDatagram);
            int len;
            byte ecn;
            IPEndPoint? remote;
            try
            {
                len = EcnInterop.Receive(handle, rented, _ipv6, out ecn, out remote);
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
            _channel.Writer.TryWrite(new Received(rented, len, remote, ecn));
        }

        _channel.Writer.TryComplete();
    }

    public void Dispose()
    {
        _stopping = true;
        _socket.Dispose(); // unblocks the native receive (returns an error), so the thread exits.
        _channel.Writer.TryComplete();

        // Drain any queued datagrams back to the pool.
        while (_channel.Reader.TryRead(out Received item))
        {
            ArrayPool<byte>.Shared.Return(item.Buffer);
        }
    }

    private readonly record struct Received(byte[] Buffer, int Length, IPEndPoint RemoteEndPoint, byte Ecn);
}
