using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Agash.StreamTransport.Adaptation;

namespace Agash.StreamTransport.WebRtc.Ice;

/// <summary>The outcome of an <see cref="IIceSocket"/> receive: the bytes read and who sent them.</summary>
public readonly record struct IceReceiveResult(
    int Length,
    IPEndPoint RemoteEndPoint,
    byte TrafficClass = 0
)
{
    /// <summary>The ECN codepoint the datagram arrived with: the traffic class's lower two bits.</summary>
    public byte Ecn => (byte)(TrafficClass & 0x03);
}

/// <summary>
/// ICE timing: check pacing (RFC 8445 appendix B.1), liveness, keep-alive cadence and consent (RFC 7675).
/// The defaults are libwebrtc's; a mobile profile shortens <see cref="ReceivingTimeout"/> so a dead path
/// is left sooner, and tests shorten everything to run failover without real-time waits.
/// </summary>
public sealed record IceTimings
{
    /// <summary>The production timing.</summary>
    public static IceTimings Default { get; } = new();

    /// <summary>The pacing between connectivity checks: one per interval. 50 ms.</summary>
    public TimeSpan Ta { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>The retransmission timeout of a connectivity check. 500 ms.</summary>
    public TimeSpan CheckRto { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long a pair may go without receiving anything, media or checks, before it counts as not
    /// receiving: the selected pair is then weak, every valid pair is pinged fast, and the agent moves to
    /// one that is receiving. 2.5 s.
    /// </summary>
    public TimeSpan ReceivingTimeout { get; init; } = TimeSpan.FromMilliseconds(2500);

    /// <summary>
    /// How often the selected pair, the one media goes on, is checked for consent: each period drawn from
    /// 0.8 to 1.2 times this (RFC 7675 section 5.1, which sets 5 s and forbids less than 4 s). Its liveness
    /// comes from the media and RTCP arriving on it; checks only renew consent. 5 s.
    /// </summary>
    public TimeSpan ConsentInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often the other valid pairs, which carry no media, are checked while the selected pair is weak,
    /// so a failover finds them receiving. 900 ms.
    /// </summary>
    public TimeSpan WeakPingInterval { get; init; } = TimeSpan.FromMilliseconds(900);

    /// <summary>
    /// How often the valid pairs other than the selected one are pinged while the selected pair is strong,
    /// so a failover finds them warm. 5 s.
    /// </summary>
    public TimeSpan StandbyPingInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long two pairs' receiving states must have held before the agent switches to the one that is
    /// receiving over a higher-ranked one that is not, against flapping. 1 s.
    /// </summary>
    public TimeSpan SwitchingDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long without a response to its checks before the selected pair loses consent. 30 s.</summary>
    public TimeSpan ConsentTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long checks run with no pair selected before the agent reports
    /// <see cref="IceConnectionState.Failed"/>, even once every pair has failed: RFC 8863's PAC timer, the
    /// time a peer's checks need to reveal a peer-reflexive candidate. 39.5 s, a check transaction with
    /// all its retransmissions.
    /// </summary>
    public TimeSpan FailureTimeout { get; init; } = TimeSpan.FromSeconds(39.5);
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

    /// <summary>Sends a datagram to <paramref name="destination"/>, marked as given.</summary>
    /// <param name="data">The datagram.</param>
    /// <param name="destination">Where it goes.</param>
    /// <param name="marking">
    /// Its traffic class: the DSCP (RFC 8837), and the ECN codepoint, ECT only for media on a validated path,
    /// never for STUN, DTLS or RTCP (RFC 6679 section 7.3.1). A socket that cannot mark sends it unmarked.
    /// </param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the datagram is handed to the network.</returns>
    ValueTask SendAsync(
        ReadOnlyMemory<byte> data,
        IPEndPoint destination,
        IpMarking marking,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Sends <paramref name="length"/> bytes at <paramref name="offset"/> of <paramref name="buffer"/>,
    /// where the bytes before the datagram, and up to <see cref="IceAgent.SendTailroom"/> after it, are
    /// free: a socket that wraps datagrams (a TURN relay) writes its framing there instead of copying.
    /// </summary>
    /// <param name="buffer">The buffer, the datagram inside it.</param>
    /// <param name="offset">Where the datagram starts.</param>
    /// <param name="length">The datagram's length.</param>
    /// <param name="destination">Where to.</param>
    /// <param name="marking">Its DSCP and ECN codepoint.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the datagram is handed over.</returns>
    ValueTask SendInPlaceAsync(
        Memory<byte> buffer,
        int offset,
        int length,
        IPEndPoint destination,
        IpMarking marking,
        CancellationToken cancellationToken
    ) => SendAsync(buffer.Slice(offset, length), destination, marking, cancellationToken);

    /// <summary>Receive the next datagram into <paramref name="buffer"/>.</summary>
    ValueTask<IceReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken
    );
}

/// <summary>What a socket can do with the ECN field (RFC 6679 section 6.1's <c>mode</c>).</summary>
public enum EcnSupport
{
    /// <summary>Neither set nor read.</summary>
    None,

    /// <summary>Can mark sent datagrams but not read received marks.</summary>
    SetOnly,

    /// <summary>Can read received marks but not mark sent datagrams.</summary>
    ReadOnly,

    /// <summary>Both.</summary>
    SetRead,
}

/// <summary>Enumerates local addresses and binds <see cref="IIceSocket"/>s for ICE host-candidate gathering.</summary>
public interface IIceSocketFactory
{
    /// <summary>What the sockets it binds can do with the ECN field.</summary>
    EcnSupport Ecn => EcnSupport.None;

    /// <summary>The local addresses to gather host candidates on (loopback included only when asked).</summary>
    IEnumerable<IPAddress> GetLocalAddresses(bool includeLoopback);

    /// <summary>Bind a datagram socket to <paramref name="address"/> (ephemeral port). False if not bindable.</summary>
    bool TryBind(IPAddress address, out IIceSocket socket);
}

/// <summary>The production <see cref="IIceSocketFactory"/>: real UDP sockets over the host's interfaces.</summary>
public sealed class UdpIceSocketFactory : IIceSocketFactory
{
    private readonly IReadOnlyList<string> _preferences;

    /// <summary>Gather on every usable local address.</summary>
    public UdpIceSocketFactory()
        : this([]) { }

    /// <summary>Gather only on local addresses matching <paramref name="localAddressPreferences"/> (see
    /// <see cref="LocalAddressFilter"/>). An empty list gathers everything.</summary>
    public UdpIceSocketFactory(IReadOnlyList<string> localAddressPreferences) =>
        _preferences = localAddressPreferences ?? [];

    /// <inheritdoc/>
    public EcnSupport Ecn =>
        (EcnInterop.NativeSendSupported, EcnInterop.NativeReceiveSupported) switch
        {
            (true, true) => EcnSupport.SetRead,
            (true, false) => EcnSupport.SetOnly,
            (false, true) => EcnSupport.ReadOnly,
            _ => EcnSupport.None,
        };

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
                if (
                    a.AddressFamily
                    is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                )
                {
                    continue;
                }

                if (
                    !LocalAddressFilter.Includes(_preferences, nic.Name, nic.Id, nic.Description, a)
                )
                {
                    continue;
                }

                if (seen.Add(a))
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
            ForbidFragmentation(raw);
            // The native socket reads and sets the ECN field per datagram (recvmsg/sendmsg on POSIX, WSARecvMsg
            // and WSASendMsg on Windows 10 build 20348 and later); older Windows gets the managed socket.
            socket = EcnInterop.NativeReceiveSupported
                ? new EcnUdpSocket(raw)
                : new UdpIceSocket(raw);
            return true;
        }
        catch (SocketException)
        {
            socket = null!;
            return false; // family unavailable / address not bindable.
        }
    }

    // RFC 8899 section 3: datagrams leave with the Don't Fragment bit (IPv4) and are never fragmented by this
    // host (IPv6), so a path MTU probe larger than the path is lost rather than fragmented, delivered and
    // taken as proof of a larger MTU. Linux sets it on UDP by default; Windows and macOS do not. An
    // oversize send then fails with EMSGSIZE, which the sender treats as a lost packet.
    internal static bool ForbidFragmentation(Socket socket)
    {
        try
        {
            if (socket.AddressFamily == AddressFamily.InterNetworkV6)
            {
                EcnInterop.SetIpv6DontFragment(socket);
            }
            else
            {
                socket.DontFragment = true;
            }

            return true;
        }
        catch (Exception exception) when (exception is SocketException or NotSupportedException)
        {
            // Deliberately not logged: a stack without the option fragments as before, which path MTU
            // discovery then cannot see; it would log once for every socket on such a system.
            return false;
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
                LocalEndPoint.AddressFamily == AddressFamily.InterNetworkV6
                    ? IPAddress.IPv6Any
                    : IPAddress.Any,
                0
            );
        }

        public IPEndPoint LocalEndPoint { get; }

        public async ValueTask SendAsync(
            ReadOnlyMemory<byte> data,
            IPEndPoint destination,
            IpMarking marking,
            CancellationToken cancellationToken = default
        ) =>
            await _socket
                .SendToAsync(data, SocketFlags.None, destination, cancellationToken)
                .ConfigureAwait(false);

        // The managed Socket API does not surface the received ECN field (ReceiveMessageFrom exposes only
        // IPPacketInformation, never the control messages that carry it), so this socket reports Not-ECT and
        // the session runs without ECN (RFC 6679 section 7.2's fallback).
        public async ValueTask<IceReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken
        )
        {
            SocketReceiveFromResult result = await _socket
                .ReceiveFromAsync(buffer, SocketFlags.None, _receiveFrom, cancellationToken)
                .ConfigureAwait(false);
            return new IceReceiveResult(result.ReceivedBytes, (IPEndPoint)result.RemoteEndPoint);
        }

        public void Dispose() => _socket.Dispose();
    }
}

/// <summary>
/// Matches a local address against the user's ICE gathering preferences. A selector is a NIC name/id/description,
/// a literal IP address, or the family keyword <c>ipv4</c>/<c>ipv6</c> (all case-insensitive). An address is
/// included if the preference list is empty (no restriction) or it matches at least one selector.
/// </summary>
public static class LocalAddressFilter
{
    /// <summary>True if <paramref name="address"/> on the given NIC should be gathered under <paramref name="selectors"/>
    /// (empty selectors include everything; otherwise a match on family keyword, literal IP, or NIC name/id/description).</summary>
    public static bool Includes(
        IReadOnlyList<string> selectors,
        string nicName,
        string nicId,
        string nicDescription,
        IPAddress address
    )
    {
        if (selectors.Count == 0)
        {
            return true;
        }

        foreach (string selector in selectors)
        {
            if (selector.Equals("ipv4", StringComparison.OrdinalIgnoreCase))
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    return true;
                }

                continue;
            }

            if (selector.Equals("ipv6", StringComparison.OrdinalIgnoreCase))
            {
                if (address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    return true;
                }

                continue;
            }

            if (IPAddress.TryParse(selector, out IPAddress? ip) && ip.Equals(address))
            {
                return true;
            }

            if (
                nicName.Equals(selector, StringComparison.OrdinalIgnoreCase)
                || nicId.Equals(selector, StringComparison.OrdinalIgnoreCase)
                || nicDescription.Equals(selector, StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        return false;
    }
}
