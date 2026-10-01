using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Agash.StreamTransport.Stun;
using Agash.StreamTransport.WebRtc.Stun;

namespace Agash.StreamTransport.Tests;

[TestClass]
public sealed class StunBindingServerTests
{
    [TestMethod]
    [DataRow("::1")]
    [DataRow("127.0.0.1")]
    public async Task AnyAddress_ReflectsSendersOfBothFamilies(string client)
    {
        Assert.IsTrue(Socket.OSSupportsIPv6, "the dual-stack server needs IPv6 on the host");
        await using StunBindingServer server = new(StunBindingServer.AnyAddress(0));
        server.Start();
        var address = IPAddress.Parse(client);
        using UdpClient udp = new(new IPEndPoint(address, 0));

        byte[] transaction = new byte[12];
        RandomNumberGenerator.Fill(transaction);
        byte[] request = new byte[64];
        StunMessageWriter writer = new(
            request,
            StunMessageClass.Request,
            StunMethod.Binding,
            transaction
        );
        writer.AddFingerprint();
        await udp.SendAsync(
            request.AsMemory(0, writer.Length),
            new IPEndPoint(address, server.ListenEndPoint.Port),
            TestContext.CancellationToken
        );
        UdpReceiveResult response = await udp.ReceiveAsync(TestContext.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);

        Assert.IsTrue(StunMessageReader.TryParse(response.Buffer, out StunMessageReader reader));
        Assert.IsTrue(reader.TryGetXorMappedAddress(out IPEndPoint mapped));
        Assert.AreEqual(udp.Client.LocalEndPoint, mapped);
    }

    public TestContext TestContext { get; set; } = null!;
}
