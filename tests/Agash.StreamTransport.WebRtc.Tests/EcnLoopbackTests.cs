using System.Net;
using System.Net.Sockets;
using Agash.StreamTransport.WebRtc.Ice;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// Verifies the native ECN socket path on the current OS for both address families and every ECN codepoint
/// (RFC 3168 §5): the sender stamps the codepoint into the outgoing TOS / Traffic-Class field and the receiver
/// must read exactly that value back from the ancillary cmsg returned by recvmsg. This catches platform-specific
/// struct-layout, option-number, cmsg-type and source-address parsing mistakes per family. Skips on platforms
/// without a native ECN receive path (Windows), matching libwebrtc's POSIX-only ECN support.
/// </summary>
[TestClass]
public sealed class EcnLoopbackTests
{
    [TestMethod]
    [DataRow(false, (byte)0x01, DisplayName = "IPv4 ECT(1)")]
    [DataRow(false, (byte)0x02, DisplayName = "IPv4 ECT(0)")]
    [DataRow(false, (byte)0x03, DisplayName = "IPv4 CE")]
    [DataRow(true, (byte)0x01, DisplayName = "IPv6 ECT(1)")]
    [DataRow(true, (byte)0x02, DisplayName = "IPv6 ECT(0)")]
    [DataRow(true, (byte)0x03, DisplayName = "IPv6 CE")]
    [Timeout(10_000)]
    public async Task NativeSocket_ReadsBackEcnCodepoint(bool ipv6, byte codepoint)
    {
        if (!EcnInterop.NativeReceiveSupported)
        {
            Assert.Inconclusive("The OS/socket stack did not expose a native ECN receive API (expected on Windows).");
        }

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
        EcnInterop.SetOutgoingEcn(tx, ipv6, codepoint);

        byte[] payload = [0x10, 0x20, 0x30, 0x40];
        tx.SendTo(payload, rx.LocalEndPoint);

        byte[] buffer = new byte[2048];
        IceReceiveResult result = await rx.ReceiveAsync(buffer, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(payload.Length, result.Length);
        CollectionAssert.AreEqual(payload, buffer.AsSpan(0, result.Length).ToArray());
        Assert.AreEqual(((IPEndPoint)tx.LocalEndPoint!).Port, result.RemoteEndPoint.Port, "source port from the parsed sockaddr");
        Assert.AreEqual(codepoint, result.Ecn, $"expected ECN codepoint 0b{Convert.ToString(codepoint, 2).PadLeft(2, '0')} read back from the cmsg");
    }
}
