using System.Net;
using System.Net.Sockets;
using Agash.StreamTransport.WebRtc.Ice;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// Verifies the native ECN socket path on the current OS for both address families (RFC 3168 section 5): the
/// sender marks the datagram and the receiver must read exactly that mark back from the control message. This
/// catches platform-specific struct-layout, option-number, control-message and source-address parsing mistakes
/// per family.
/// </summary>
[TestClass]
public sealed class EcnLoopbackTests
{
    [TestMethod]
    [DataRow(false, (byte)0x01, DisplayName = "IPv4 ECT(1)")]
    [DataRow(false, (byte)0x02, DisplayName = "IPv4 ECT(0)")]
    [DataRow(true, (byte)0x01, DisplayName = "IPv6 ECT(1)")]
    [DataRow(true, (byte)0x02, DisplayName = "IPv6 ECT(0)")]
    [Timeout(10_000)]
    public Task NativeSocket_ReadsBackEctCodepoint(bool ipv6, byte codepoint) =>
        SendAndReadBackAsync(ipv6, codepoint);

    // Only the network marks CE; Windows refuses to send it, so a host can make it only on Linux and macOS.
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [DataRow(false, DisplayName = "IPv4 CE")]
    [DataRow(true, DisplayName = "IPv6 CE")]
    [Timeout(10_000)]
    public Task NativeSocket_ReadsBackCe(bool ipv6) => SendAndReadBackAsync(ipv6, 0x03);

    // Each datagram carries its own mark (RFC 6679 section 7.3.1: media may be ECT while RTCP, STUN and DTLS
    // on the same socket are not), so marks set one after another on one socket arrive as sent.
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX | OperatingSystems.Windows)]
    [DataRow(false, DisplayName = "IPv4")]
    [DataRow(true, DisplayName = "IPv6")]
    [Timeout(10_000)]
    public async Task NativeSocket_MarksEachDatagramOnItsOwn(bool ipv6)
    {
        IPAddress loopback = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        if (ipv6 && !Socket.OSSupportsIPv6)
        {
            Assert.Inconclusive("IPv6 is not available on this host.");
        }

        using var rxRaw = new Socket(loopback.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        rxRaw.Bind(new IPEndPoint(loopback, 0));
        using var rx = new EcnUdpSocket(rxRaw);
        using var tx = new Socket(loopback.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        tx.Bind(new IPEndPoint(loopback, 0));

        byte[] marks = [0x01, 0x00, 0x02, 0x00, 0x01];
        for (int i = 0; i < marks.Length; i++)
        {
            EcnInterop.Send(tx, [(byte)i], rx.LocalEndPoint, marks[i]);
        }

        byte[] buffer = new byte[2048];
        for (int i = 0; i < marks.Length; i++)
        {
            IceReceiveResult result = await rx.ReceiveAsync(buffer, CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(marks[buffer[0]], result.Ecn, $"datagram {buffer[0]}");
        }
    }

    private static async Task SendAndReadBackAsync(bool ipv6, byte codepoint)
    {
        IPAddress loopback = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        if (ipv6 && !Socket.OSSupportsIPv6)
        {
            Assert.Inconclusive("IPv6 is not available on this host.");
        }

        using var rxRaw = new Socket(loopback.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        rxRaw.Bind(new IPEndPoint(loopback, 0));
        using var rx = new EcnUdpSocket(rxRaw);

        using var tx = new Socket(loopback.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        tx.Bind(new IPEndPoint(loopback, 0));

        byte[] payload = [0x10, 0x20, 0x30, 0x40];
        EcnInterop.Send(tx, payload, rx.LocalEndPoint, codepoint);

        byte[] buffer = new byte[2048];
        IceReceiveResult result = await rx.ReceiveAsync(buffer, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(payload.Length, result.Length);
        CollectionAssert.AreEqual(payload, buffer.AsSpan(0, result.Length).ToArray());
        Assert.AreEqual(
            ((IPEndPoint)tx.LocalEndPoint!).Port,
            result.RemoteEndPoint.Port,
            "source port from the parsed sockaddr"
        );
        Assert.AreEqual(
            codepoint,
            result.Ecn,
            $"expected ECN codepoint 0b{Convert.ToString(codepoint, 2).PadLeft(2, '0')} read back from the control message"
        );
    }
}
