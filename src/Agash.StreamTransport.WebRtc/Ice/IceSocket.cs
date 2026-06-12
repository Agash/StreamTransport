using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Agash.StreamTransport.WebRtc.Ice;

/// <summary>The outcome of an <see cref="IIceSocket"/> receive: the bytes read and who sent them.</summary>
public readonly record struct IceReceiveResult(int Length, IPEndPoint RemoteEndPoint);

/// <summary>
/// ICE check/consent timing (RFC 8445 Appendix B.1 pacing + RFC 7675 consent). <see cref="Default"/> is the
/// production setting; tests use short values to exercise consent loss / failover without real-time waits, and
/// a profile could tighten consent for a mobile link.
/// </summary>
/// <param name="Ta">Connectivity-check pacing interval.</param>
/// <param name="CheckRto">Per-check retransmit timeout.</param>
/// <param name="ConsentInterval">How often consent (and hot-standby keep-warm) pings are sent.</param>
/// <param name="ConsentTimeout">How long without a response before the selected pair is declared dead.</param>
public sealed record IceTimings(TimeSpan Ta, TimeSpan CheckRto, TimeSpan ConsentInterval, TimeSpan ConsentTimeout)
{
    /// <summary>The production timing (RFC defaults): 50 ms pacing, 500 ms RTO, 5 s consent, 30 s timeout.</summary>
    public static IceTimings Default { get; } = new(
        TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
}

/// <summary>
/// A bound datagram endpoint the <see cref="IceAgent"/> sends checks/media over and receives on. Abstracting
/// it (rather than using <see cref="Socket"/> directly) lets tests drive ICE - candidate switching, path
/// failover - over an in-memory network deterministically, with no real sockets or wall-clock timing.
/// </summary>
public interface IIceSocket : IDisposable
{
    /// <summary>The local address/port this socket is bound to.</summary>
    IPEndPoint LocalEndPoint { get; }

    /// <summary>Send a datagram to <paramref name="destination"/>.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, IPEndPoint destination, CancellationToken cancellationToken = default);

    /// <summary>Receive the next datagram into <paramref name="buffer"/>.</summary>
    ValueTask<IceReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

/// <summary>Enumerates local addresses and binds <see cref="IIceSocket"/>s for ICE host-candidate gathering.</summary>
public interface IIceSocketFactory
{
    /// <summary>The local addresses to gather host candidates on (loopback included only when asked).</summary>
    IEnumerable<IPAddress> GetLocalAddresses(bool includeLoopback);

    /// <summary>Bind a datagram socket to <paramref name="address"/> (ephemeral port). False if not bindable.</summary>
    bool TryBind(IPAddress address, out IIceSocket socket);
}

/// <summary>The production <see cref="IIceSocketFactory"/>: real UDP sockets over the host's interfaces.</summary>
public sealed class UdpIceSocketFactory : IIceSocketFactory
{
    /// <inheritdoc/>
    public IEnumerable<IPAddress> GetLocalAddresses(bool includeLoopback)
    {
        var seen = new HashSet<IPAddress>();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback && !includeLoopback)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
            {
                IPAddress a = info.Address;
                if (a.AddressFamily is (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) && seen.Add(a))
                {
                    yield return a;
                }
            }
        }
    }

    /// <inheritdoc/>
    public bool TryBind(IPAddress address, out IIceSocket socket)
    {
        try
        {
            var raw = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            raw.Bind(new IPEndPoint(address, 0));
            socket = new UdpIceSocket(raw);
            return true;
        }
        catch (SocketException)
        {
            socket = null!;
            return false; // family unavailable / address not bindable.
        }
    }

    private sealed class UdpIceSocket : IIceSocket
    {
        private readonly Socket _socket;
        private readonly EndPoint _receiveFrom;

        public UdpIceSocket(Socket socket)
        {
            _socket = socket;
            LocalEndPoint = (IPEndPoint)socket.LocalEndPoint!;
            _receiveFrom = new IPEndPoint(
                LocalEndPoint.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        }

        public IPEndPoint LocalEndPoint { get; }

        public async ValueTask SendAsync(ReadOnlyMemory<byte> data, IPEndPoint destination, CancellationToken cancellationToken = default) =>
            await _socket.SendToAsync(data, SocketFlags.None, destination, cancellationToken).ConfigureAwait(false);

        public async ValueTask<IceReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            SocketReceiveFromResult result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, _receiveFrom, cancellationToken).ConfigureAwait(false);
            return new IceReceiveResult(result.ReceivedBytes, (IPEndPoint)result.RemoteEndPoint);
        }

        public void Dispose() => _socket.Dispose();
    }
}
