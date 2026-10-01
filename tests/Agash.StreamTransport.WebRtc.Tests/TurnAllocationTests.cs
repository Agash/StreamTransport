using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Agash.StreamTransport.WebRtc.Ice;
using Agash.StreamTransport.WebRtc.Turn;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class TurnAllocationTests
{
    [TestMethod]
    [DataRow(TurnTransport.Udp)]
    [DataRow(TurnTransport.Tcp)]
    [DataRow(TurnTransport.Tls)]
    [Timeout(20_000)]
    public async Task Allocation_EveryTransport_RelaysBothWaysAndMovesToAChannel(
        TurnTransport transport
    )
    {
        await using var server = new TestTurnServer(IPAddress.Loopback, tls: true);
        using TurnAllocation allocation = await Allocate(server, transport, TimeProvider.System);
        using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndPoint = (IPEndPoint)peer.LocalEndPoint!;
        Channel<(byte[] Data, IPEndPoint Source)> received = Pump(allocation);

        Assert.AreNotEqual(server.UdpEndPoint, allocation.LocalEndPoint);
        Assert.AreEqual(IPAddress.Loopback, allocation.MappedAddress.Address);

        // Sends repeat until the bind installs the permission, as ICE checks do.
        byte[] buffer = new byte[64];
        SocketReceiveFromResult arrived = default;
        for (int attempt = 0; attempt < 50 && arrived.ReceivedBytes == 0; attempt++)
        {
            await allocation.SendAsync("ping"u8.ToArray(), peerEndPoint);
            using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try
            {
                arrived = await peer.ReceiveFromAsync(
                    buffer,
                    SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0),
                    wait.Token
                );
            }
            catch (OperationCanceledException)
            {
                // Not through yet.
            }
        }

        Assert.AreEqual("ping", System.Text.Encoding.ASCII.GetString(buffer, 0, arrived.ReceivedBytes));
        Assert.AreEqual(allocation.LocalEndPoint, arrived.RemoteEndPoint);

        _ = await peer.SendToAsync("pong"u8.ToArray(), SocketFlags.None, allocation.LocalEndPoint);
        (byte[] data, IPEndPoint source) = await received.Reader.ReadAsync();
        Assert.AreEqual("pong", System.Text.Encoding.ASCII.GetString(data));
        Assert.AreEqual(peerEndPoint, source);

        await allocation.SendAsync("again"u8.ToArray(), peerEndPoint);
        SocketReceiveFromResult again = await peer.ReceiveFromAsync(
            buffer,
            SocketFlags.None,
            new IPEndPoint(IPAddress.Any, 0)
        );
        Assert.AreEqual("again", System.Text.Encoding.ASCII.GetString(buffer, 0, again.ReceivedBytes));
        Assert.AreEqual(1, server.ChannelBinds);
        Assert.IsGreaterThan(0, server.ChannelDataFrames);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task AllocateAsync_WrongPassword_IsRefused()
    {
        await using var server = new TestTurnServer(IPAddress.Loopback);
        var turn = new TurnServer("127.0.0.1", server.UdpEndPoint.Port, TurnTransport.Udp, TestTurnServer.Username, "wrong");

        TurnException refused = await Assert.ThrowsExactlyAsync<TurnException>(() =>
            TurnAllocation.AllocateAsync(turn, server.UdpEndPoint, TimeProvider.System, NullLogger.Instance, CancellationToken.None)
        );
        Assert.AreEqual(401, refused.Code);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ChannelBind_StaleNonce_RetriesWithTheFreshOne()
    {
        await using var server = new TestTurnServer(IPAddress.Loopback, staleNonces: 1);
        using TurnAllocation allocation = await Allocate(server, TurnTransport.Udp, TimeProvider.System);
        _ = Pump(allocation);
        var peer = new IPEndPoint(IPAddress.Loopback, 9);

        for (int attempt = 0; attempt < 50 && server.ChannelBinds == 0; attempt++)
        {
            await allocation.SendAsync("x"u8.ToArray(), peer);
            await Task.Delay(20);
        }

        Assert.AreEqual(1, server.ChannelBinds);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Maintenance_RefreshesTheAllocationAndBindingsBeforeTheyLapse()
    {
        var time = new FakeTimeProvider();
        await using var server = new TestTurnServer(IPAddress.Loopback);
        using TurnAllocation allocation = await Allocate(server, TurnTransport.Udp, time);
        _ = Pump(allocation);
        var peer = new IPEndPoint(IPAddress.Loopback, 9);
        for (int attempt = 0; attempt < 50 && server.ChannelBinds == 0; attempt++)
        {
            await allocation.SendAsync("x"u8.ToArray(), peer);
            await Task.Delay(20);
        }

        // Past four minutes the binding is refreshed; past 7.5 the 600 s allocation is. Each step lets
        // the round trips it started finish before the clock moves on, so no request times out.
        for (int minute = 0; minute < 8; minute++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(100);
        }

        for (int wait = 0; wait < 100 && server.Refreshes == 0; wait++)
        {
            await Task.Delay(10);
        }

        Assert.IsGreaterThanOrEqualTo(2, server.ChannelBinds);
        Assert.AreEqual(1, server.Refreshes);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Dispose_ReleasesTheAllocation()
    {
        await using var server = new TestTurnServer(IPAddress.Loopback);
        TurnAllocation allocation = await Allocate(server, TurnTransport.Udp, TimeProvider.System);

        allocation.Dispose();
        for (int wait = 0; wait < 100 && server.Deallocations == 0; wait++)
        {
            await Task.Delay(10);
        }

        Assert.AreEqual(1, server.Deallocations);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task RelayPolicy_TwoAgents_ConnectThroughTurnAndCarryData()
    {
        await using var server = new TestTurnServer(IPAddress.Loopback);
        var turn = new TurnServer(
            "127.0.0.1",
            server.UdpEndPoint.Port,
            TurnTransport.Udp,
            TestTurnServer.Username,
            TestTurnServer.Password
        );
        var offererCredentials = IceCredentials.Generate();
        var answererCredentials = IceCredentials.Generate();
        await using var offerer = new IceAgent(
            offererCredentials,
            IceRole.Controlling,
            transportPolicy: IceTransportPolicy.Relay
        );
        await using var answerer = new IceAgent(
            answererCredentials,
            IceRole.Controlled,
            transportPolicy: IceTransportPolicy.Relay
        );
        offerer.AddTurnServer(turn);
        answerer.AddTurnServer(turn);
        offerer.SetRemoteCredentials(answererCredentials);
        answerer.SetRemoteCredentials(offererCredentials);
        List<IceCandidate> gathered = [];
        offerer.LocalCandidateGathered += c =>
        {
            lock (gathered)
            {
                gathered.Add(c);
            }

            answerer.AddRemoteCandidate(c);
        };
        answerer.LocalCandidateGathered += offerer.AddRemoteCandidate;
        TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        offerer.StateChanged += s =>
        {
            if (s == IceConnectionState.Connected)
            {
                connected.TrySetResult();
            }
        };
        TaskCompletionSource<string> data = new(TaskCreationOptions.RunContinuationsAsynchronously);
        answerer.DataReceived += (packet, _, _) =>
            data.TrySetResult(System.Text.Encoding.ASCII.GetString(packet.Span[1..]));

        offerer.Start();
        answerer.Start();
        await connected.Task;
        for (int attempt = 0; attempt < 100 && !data.Task.IsCompleted; attempt++)
        {
            // The first byte puts the datagram in the RTP range of the demultiplexer.
            await offerer.SendAsync(new byte[] { 0x80, (byte)'m', (byte)'e', (byte)'d', (byte)'i', (byte)'a' });
            await Task.WhenAny(data.Task, Task.Delay(50));
        }

        Assert.AreEqual("media", await data.Task);
        lock (gathered)
        {
            Assert.IsTrue(gathered.TrueForAll(static c => c.Kind == IceCandidateKind.Relayed));
            Assert.IsNotEmpty(gathered);
        }

        Assert.IsGreaterThan(0, server.ChannelDataFrames);
    }

    [TestMethod]
    [DataRow("turn:turn.example.com", "turn.example.com", 3478, TurnTransport.Udp)]
    [DataRow("turn:turn.example.com:3479?transport=tcp", "turn.example.com", 3479, TurnTransport.Tcp)]
    [DataRow("turns:turn.example.com", "turn.example.com", 5349, TurnTransport.Tls)]
    [DataRow("turns:turn.example.com:443?transport=tcp", "turn.example.com", 443, TurnTransport.Tls)]
    [DataRow("turn:[2001:db8::1]:3478?transport=udp", "2001:db8::1", 3478, TurnTransport.Udp)]
    [DataRow("TURN:192.0.2.1", "192.0.2.1", 3478, TurnTransport.Udp)]
    public void TryParse_TurnUris_ReadHostPortAndTransport(
        string uri,
        string host,
        int port,
        TurnTransport transport
    )
    {
        Assert.IsTrue(TurnServer.TryParse(uri, "u", "p", out TurnServer server));
        Assert.AreEqual(host, server.Host);
        Assert.AreEqual(port, server.Port);
        Assert.AreEqual(transport, server.Transport);
        Assert.AreEqual("u", server.Username);
        Assert.AreEqual("p", server.Credential);
    }

    [TestMethod]
    [DataRow("stun:stun.example.com")]
    [DataRow("turns:turn.example.com?transport=udp")]
    [DataRow("turn:turn.example.com:0")]
    [DataRow("turn:")]
    [DataRow("turn:host?transport=sctp")]
    public void TryParse_UnsupportedUris_AreRejected(string uri) =>
        Assert.IsFalse(TurnServer.TryParse(uri, "u", "p", out _));

    private static Task<TurnAllocation> Allocate(
        TestTurnServer server,
        TurnTransport transport,
        TimeProvider time
    )
    {
        IPEndPoint endpoint = transport switch
        {
            TurnTransport.Udp => server.UdpEndPoint,
            TurnTransport.Tcp => server.TcpEndPoint,
            _ => server.TlsEndPoint,
        };
        var turn = new TurnServer(
            "turn.test",
            endpoint.Port,
            transport,
            TestTurnServer.Username,
            TestTurnServer.Password
        )
        {
            CertificateValidation = static (_, _, _, _) => true,
        };
        return TurnAllocation.AllocateAsync(
            turn,
            endpoint,
            time,
            NullLogger.Instance,
            CancellationToken.None
        );
    }

    // Drives the allocation as the ICE receive loop does, so its transactions complete.
    private static Channel<(byte[] Data, IPEndPoint Source)> Pump(TurnAllocation allocation)
    {
        var received = Channel.CreateUnbounded<(byte[], IPEndPoint)>();
        _ = Task.Run(async () =>
        {
            byte[] buffer = new byte[2048];
            try
            {
                while (true)
                {
                    IceReceiveResult result = await allocation.ReceiveAsync(buffer, CancellationToken.None);
                    received.Writer.TryWrite((buffer[..result.Length], result.RemoteEndPoint));
                }
            }
            catch (Exception exception) when (exception is ObjectDisposedException or SocketException or IOException or OperationCanceledException)
            {
                received.Writer.TryComplete();
            }
        });
        return received;
    }
}
