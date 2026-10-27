using System.Net;
using Agash.StreamTransport.WebRtc.Ice;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// The source address a socket last received from is parsed once and handed back while datagrams keep
/// coming from it; a new source is parsed afresh, with an IPv6 link-local address keeping its scope.
/// </summary>
[TestClass]
public sealed class SenderCacheTests
{
    [TestMethod]
    public void Resolve_SameSource_ReturnsTheSameEndpoint()
    {
        EcnInterop.SenderCache senders = new();
        byte[] from = SockAddr6(IPAddress.Parse("2001:db8::1"), 5000, scope: 0);

        IPEndPoint first = senders.Resolve(from, ipv6: true);
        IPEndPoint again = senders.Resolve(from, ipv6: true);

        Assert.AreSame(first, again);
        Assert.AreEqual(new IPEndPoint(IPAddress.Parse("2001:db8::1"), 5000), first);
    }

    [TestMethod]
    public void Resolve_AnotherSource_IsParsedAfresh()
    {
        EcnInterop.SenderCache senders = new();
        IPEndPoint first = senders.Resolve(
            SockAddr6(IPAddress.Parse("2001:db8::1"), 5000, 0),
            ipv6: true
        );

        IPEndPoint other = senders.Resolve(
            SockAddr6(IPAddress.Parse("2001:db8::1"), 5001, 0),
            ipv6: true
        );

        Assert.AreNotSame(first, other);
        Assert.AreEqual(5001, other.Port);
    }

    [TestMethod]
    public void Resolve_LinkLocal_KeepsTheScope()
    {
        EcnInterop.SenderCache senders = new();

        IPEndPoint linkLocal = senders.Resolve(
            SockAddr6(IPAddress.Parse("fe80::1"), 5000, scope: 25),
            ipv6: true
        );

        Assert.AreEqual(25, linkLocal.Address.ScopeId, "a reply needs the interface");
    }

    [TestMethod]
    public void Resolve_IPv4()
    {
        EcnInterop.SenderCache senders = new();
        byte[] sockaddr = new byte[16];
        sockaddr[2] = 0x13;
        sockaddr[3] = 0x88;
        IPAddress.Parse("192.0.2.7").TryWriteBytes(sockaddr.AsSpan(4, 4), out _);

        Assert.AreEqual(
            new IPEndPoint(IPAddress.Parse("192.0.2.7"), 5000),
            senders.Resolve(sockaddr, ipv6: false)
        );
    }

    // A sockaddr_in6 as the kernel writes it: the family is not read, the port is big-endian, the scope
    // id little-endian at its end.
    private static byte[] SockAddr6(IPAddress address, int port, uint scope)
    {
        byte[] sockaddr = new byte[28];
        sockaddr[2] = (byte)(port >> 8);
        sockaddr[3] = (byte)port;
        address.TryWriteBytes(sockaddr.AsSpan(8, 16), out _);
        BitConverter.TryWriteBytes(sockaddr.AsSpan(24, 4), scope);
        return sockaddr;
    }
}
