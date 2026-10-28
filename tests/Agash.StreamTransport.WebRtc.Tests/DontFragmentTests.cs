using System.Net;
using System.Net.Sockets;
using Agash.StreamTransport.WebRtc.Ice;

namespace Agash.StreamTransport.WebRtc.Tests;

// RFC 8899 section 3: ICE sockets send without fragmentation, so an oversize path MTU probe is lost and
// cannot confirm a size the path does not carry.
[TestClass]
public sealed class DontFragmentTests
{
    [TestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("::1")]
    public void IceSockets_ForbidFragmentation(string address)
    {
        var ip = IPAddress.Parse(address);
        using Socket socket = new(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(ip, 0));
        }
        catch (SocketException)
        {
            Assert.Inconclusive($"{address} cannot be bound here.");
        }

        Assert.IsTrue(UdpIceSocketFactory.ForbidFragmentation(socket));
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            Assert.IsTrue(socket.DontFragment);
        }
    }
}
