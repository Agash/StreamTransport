using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Agash.StreamTransport.Stun;

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

    // RFC 8489 section 6.3.1: an unknown comprehension-required attribute is answered with 420 naming it.
    [TestMethod]
    public async Task UnknownRequiredAttribute_IsAnswered420()
    {
        await using StunBindingServer server = new(new IPEndPoint(IPAddress.Loopback, 0));
        server.Start();
        using UdpClient udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] request = new byte[64];
        StunMessageWriter writer = new(
            request,
            StunMessageClass.Request,
            StunMethod.Binding,
            RandomNumberGenerator.GetBytes(12)
        );
        writer.AddAttribute((StunAttributeType)0x7F01, [1, 2, 3, 4]);
        writer.AddFingerprint();
        await udp.SendAsync(
            request.AsMemory(0, writer.Length),
            new IPEndPoint(IPAddress.Loopback, server.ListenEndPoint.Port),
            TestContext.CancellationToken
        );
        UdpReceiveResult response = await udp.ReceiveAsync(TestContext.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);

        Assert.IsTrue(StunMessageReader.TryParse(response.Buffer, out StunMessageReader reader));
        Assert.AreEqual(StunMessageClass.ErrorResponse, reader.Class);
        Assert.IsTrue(reader.TryGetErrorCode(out int code));
        Assert.AreEqual(420, code);
        Assert.IsTrue(
            reader.TryFindAttribute(
                StunAttributeType.UnknownAttributes,
                out ReadOnlySpan<byte> types
            )
        );
        Assert.AreEqual(0x7F01, (types[0] << 8) | types[1]);
    }

    public TestContext TestContext { get; set; } = null!;
}
