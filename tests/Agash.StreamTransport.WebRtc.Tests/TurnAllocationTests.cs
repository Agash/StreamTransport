using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.WebRtc.Ice;
using Agash.StreamTransport.WebRtc.Turn;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class TurnAllocationTests
{
    public TestContext TestContext { get; set; } = null!;

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
            await allocation.SendAsync("ping"u8.ToArray(), peerEndPoint, default);
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

        CollectionAssert.AreEqual("ping"u8.ToArray(), buffer[..arrived.ReceivedBytes]);
        Assert.AreEqual(allocation.LocalEndPoint, arrived.RemoteEndPoint);

        _ = await peer.SendToAsync("pong"u8.ToArray(), SocketFlags.None, allocation.LocalEndPoint);
        (byte[] data, IPEndPoint source) = await received.Reader.ReadAsync();
        CollectionAssert.AreEqual("pong"u8.ToArray(), data);
        Assert.AreEqual(peerEndPoint, source);

        // The server installs the permission as it handles the ChannelBind, so the first ping can arrive
        // before the client has the response; Send indications carry traffic until it does.
        for (int attempt = 0; attempt < 50 && server.ChannelDataFrames == 0; attempt++)
        {
            await allocation.SendAsync("again"u8.ToArray(), peerEndPoint, default);
            CollectionAssert.AreEqual("again"u8.ToArray(), await ReceiveSkippingPingsAsync(peer));
        }

        Assert.AreEqual(1, server.ChannelBinds);
        Assert.IsGreaterThan(0, server.ChannelDataFrames, "traffic moved to the channel");
    }

    // The next datagram that is not a ping; pings delayed past their retry arrive late and are skipped.
    private static async Task<byte[]> ReceiveSkippingPingsAsync(Socket socket)
    {
        byte[] buffer = new byte[64];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            SocketReceiveFromResult result = await socket.ReceiveFromAsync(
                buffer,
                SocketFlags.None,
                new IPEndPoint(IPAddress.Any, 0),
                timeout.Token
            );
            byte[] payload = buffer[..result.ReceivedBytes];
            if (!payload.AsSpan().SequenceEqual("ping"u8))
            {
                return payload;
            }
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task AllocateAsync_WrongPassword_IsRefused()
    {
        await using var server = new TestTurnServer(IPAddress.Loopback);
        var turn = new TurnServer(
            "127.0.0.1",
            server.UdpEndPoint.Port,
            TurnTransport.Udp,
            TestTurnServer.Username,
            "wrong"
        );

        TurnException refused = await Assert.ThrowsExactlyAsync<TurnException>(() =>
            TurnAllocation.AllocateAsync(
                turn,
                server.UdpEndPoint,
                TimeProvider.System,
                NullLogger.Instance,
                CancellationToken.None
            )
        );
        Assert.AreEqual(401, refused.Code);
        Assert.AreEqual(2, server.AllocateRequests, "the challenge answered once, then refused");
    }

    // RFC 8656 section 11.1: nothing is relayed to a peer before its permission is installed. The
    // first datagram waits for the CreatePermission and then goes, so one send is enough.
    [TestMethod]
    [Timeout(10_000)]
    public async Task SendAsync_NoPermissionYet_InstallsItBeforeRelaying()
    {
        await using var server = new TestTurnServer(IPAddress.Loopback);
        using TurnAllocation allocation = await Allocate(
            server,
            TurnTransport.Udp,
            TimeProvider.System
        );
        _ = Pump(allocation);
        using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        await allocation.SendAsync("once"u8.ToArray(), (IPEndPoint)peer.LocalEndPoint!, default);

        byte[] buffer = new byte[64];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SocketReceiveFromResult arrived = await peer.ReceiveFromAsync(
            buffer,
            SocketFlags.None,
            new IPEndPoint(IPAddress.Any, 0),
            timeout.Token
        );
        CollectionAssert.AreEqual("once"u8.ToArray(), buffer[..arrived.ReceivedBytes]);
        Assert.AreEqual(1, server.CreatePermissions);
        Assert.AreEqual(
            0,
            server.RefusedSends,
            "no Send indication went out ahead of the permission"
        );
    }

    // RFC 8656 sections 11.4 and 12.4: a datagram with free bytes in front of it is framed there, a Send
    // indication before the channel is bound and ChannelData after, and the peer gets exactly the data.
    [TestMethod]
    [Timeout(10_000)]
    public async Task SendInPlace_FramesInTheHeadroom_AndThePeerGetsTheData()
    {
        await using var server = new TestTurnServer(IPAddress.Loopback);
        using TurnAllocation allocation = await Allocate(
            server,
            TurnTransport.Udp,
            TimeProvider.System
        );
        _ = Pump(allocation);
        using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var destination = (IPEndPoint)peer.LocalEndPoint!;
        await allocation.SendAsync("permit"u8.ToArray(), destination, default);
        CollectionAssert.AreEqual("permit"u8.ToArray(), await ReceiveAsync(peer));

        HashSet<byte> framings = [];
        for (int i = 0; i < 20 && framings.Count < 2; i++)
        {
            byte[] buffer = new byte[IceAgent.SendHeadroom + 5 + IceAgent.SendTailroom];
            buffer.AsSpan(0, IceAgent.SendHeadroom).Fill(0xEE);
            "media"u8.CopyTo(buffer.AsSpan(IceAgent.SendHeadroom));

            await allocation.SendInPlaceAsync(
                buffer,
                IceAgent.SendHeadroom,
                5,
                destination,
                default,
                TestContext.CancellationToken
            );

            CollectionAssert.AreEqual("media"u8.ToArray(), await ReceiveAsync(peer));
            // A ChannelData header's first byte is 0x40 to 0x4F; a Send indication's header ends 36 bytes
            // before an IPv4 peer's data.
            byte channelByte = buffer[IceAgent.SendHeadroom - 4];
            framings.Add(
                channelByte is >= 0x40 and <= 0x4F ? (byte)1
                : buffer[IceAgent.SendHeadroom - 36] == 0x00
                && buffer[IceAgent.SendHeadroom - 35] == 0x16
                    ? (byte)2
                : (byte)0
            );
            await server.WhenChannelBound.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.CancellationToken
            );
        }

        Assert.DoesNotContain((byte)0, framings, "the frame was written in the headroom");
        Assert.Contains((byte)1, framings, "ChannelData once the channel is bound");
    }

    private async Task<byte[]> ReceiveAsync(Socket peer)
    {
        byte[] buffer = new byte[64];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken
        );
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        SocketReceiveFromResult arrived = await peer.ReceiveFromAsync(
            buffer,
            SocketFlags.None,
            new IPEndPoint(IPAddress.Any, 0),
            timeout.Token
        );
        return buffer[..arrived.ReceivedBytes];
    }

    // RFC 8656 section 11.4: a Data indication from an address with no active permission is dropped.
    [TestMethod]
    [Timeout(10_000)]
    public async Task ReceiveAsync_DataFromAnAddressWithoutPermission_IsDropped()
    {
        await using var server = new TestTurnServer(IPAddress.Loopback);
        using TurnAllocation allocation = await Allocate(
            server,
            TurnTransport.Udp,
            TimeProvider.System
        );
        Channel<(byte[] Data, IPEndPoint Source)> received = Pump(allocation);
        using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndPoint = (IPEndPoint)peer.LocalEndPoint!;
        await allocation.SendAsync("hello"u8.ToArray(), peerEndPoint, default);
        _ = await peer.ReceiveFromAsync(
                new byte[64],
                SocketFlags.None,
                new IPEndPoint(IPAddress.Any, 0)
            )
            .WaitAsync(TimeSpan.FromSeconds(5));

        await server.InjectDataAsync(
            new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9),
            "forged"u8.ToArray()
        );
        _ = await peer.SendToAsync("real"u8.ToArray(), SocketFlags.None, allocation.LocalEndPoint);

        (byte[] data, IPEndPoint source) = await received
            .Reader.ReadAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual("real"u8.ToArray(), data, "the forged datagram came first");
        Assert.AreEqual(peerEndPoint, source);
    }

    // RFC 8656 section 8.3: a 437 to a Refresh means the allocation no longer exists. The relay's
    // receive loop then fails as a closed connection does, and sends are dropped.
    [TestMethod]
    [Timeout(10_000)]
    public async Task Refresh_AllocationGone_FailsTheRelay()
    {
        var time = new FakeTimeProvider();
        await using var server = new TestTurnServer(IPAddress.Loopback);
        using TurnAllocation allocation = await Allocate(server, TurnTransport.Udp, time);
        Channel<(byte[] Data, IPEndPoint Source)> received = Pump(allocation);

        server.ForgetAllocations();
        time.Advance(TimeSpan.FromSeconds(540));

        ChannelClosedException closed = await Assert.ThrowsExactlyAsync<ChannelClosedException>(
            () =>
                received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))
        );
        Assert.IsInstanceOfType<IOException>(closed.InnerException);
        await allocation.SendAsync("x"u8.ToArray(), new IPEndPoint(IPAddress.Loopback, 9), default);
    }

    // RFC 8656 section 8: the refresh is due before the granted lifetime ends; a lifetime under two
    // minutes is refreshed at half, as libwebrtc does.
    [TestMethod]
    [Timeout(10_000)]
    public async Task Refresh_ShortLifetime_IsDueAtHalfOfIt()
    {
        var time = new FakeTimeProvider();
        await using var server = new TestTurnServer(IPAddress.Loopback, lifetime: 60);
        using TurnAllocation allocation = await Allocate(server, TurnTransport.Udp, time);
        _ = Pump(allocation);

        time.Advance(TimeSpan.FromSeconds(29));

        // Deliberate: gives a refresh that was wrongly due the chance to reach the server.
        await Task.Delay(200);
        Assert.AreEqual(0, server.Refreshes);

        time.Advance(TimeSpan.FromSeconds(1));
        await server.WhenRefreshed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, server.Refreshes);
    }

    // RFC 8656 section 7.4: a 437 to an Allocate is retried from another client port, three ports in
    // all, before giving up on the server.
    [TestMethod]
    [Timeout(10_000)]
    [DataRow(2, true)]
    [DataRow(3, false)]
    public async Task AllocateAsync_AllocationMismatch_TriesAnotherClientPort(
        int mismatches,
        bool allocates
    )
    {
        await using var server = new TestTurnServer(IPAddress.Loopback, mismatches: mismatches);
        if (allocates)
        {
            using TurnAllocation allocation = await Allocate(
                server,
                TurnTransport.Udp,
                TimeProvider.System
            );
            Assert.AreEqual(1, server.Allocations);
        }
        else
        {
            TurnException refused = await Assert.ThrowsExactlyAsync<TurnException>(() =>
                Allocate(server, TurnTransport.Udp, TimeProvider.System)
            );
            Assert.AreEqual(437, refused.Code);
            Assert.AreEqual(0, server.Allocations);
        }
    }

    // RFC 8489 section 10: a 300 sends the same request to the ALTERNATE-SERVER.
    [TestMethod]
    [Timeout(10_000)]
    public async Task AllocateAsync_TryAlternate_AllocatesOnTheAlternateServer()
    {
        await using var alternate = new TestTurnServer(IPAddress.Loopback);
        await using var server = new TestTurnServer(
            IPAddress.Loopback,
            alternate: alternate.UdpEndPoint
        );

        using TurnAllocation allocation = await Allocate(
            server,
            TurnTransport.Udp,
            TimeProvider.System
        );

        Assert.AreEqual(0, server.Allocations);
        Assert.AreEqual(1, alternate.Allocations);
    }

    // RFC 8489 section 6.2.1: over UDP a request goes out Rc = 7 times, the RTO doubling from 500 ms,
    // and the last one is waited on for Rm = 16 RTOs: 39.5 s in all.
    [TestMethod]
    [Timeout(10_000)]
    public async Task AllocateAsync_SilentServer_TransmitsSevenTimesOver39Point5Seconds()
    {
        var time = new FakeTimeProvider();
        using var silent = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp
        );
        silent.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)silent.LocalEndPoint!;
        var turn = new TurnServer("127.0.0.1", endpoint.Port, TurnTransport.Udp, "u", "p");
        Task<TurnAllocation> allocating = TurnAllocation.AllocateAsync(
            turn,
            endpoint,
            time,
            NullLogger.Instance,
            CancellationToken.None
        );

        byte[] buffer = new byte[2048];
        TimeSpan[] waits =
        [
            .. new[] { 500, 1000, 2000, 4000, 8000, 16000, 8000 }.Select(ms =>
                TimeSpan.FromMilliseconds(ms)
            ),
        ];
        foreach (TimeSpan wait in waits)
        {
            _ = await silent.ReceiveAsync(buffer).WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(wait);
        }

        TurnException failed = await Assert.ThrowsExactlyAsync<TurnException>(() =>
            allocating.WaitAsync(TimeSpan.FromSeconds(5))
        );
        Assert.AreEqual(0, failed.Code);
        Assert.AreEqual(0, silent.Available, "no eighth transmission");
    }

    // RFC 8656 section 9: permissions last 300 s, so each is refreshed with CreatePermission before
    // then, whether or not its peer has a channel.
    [TestMethod]
    [Timeout(10_000)]
    public async Task Maintenance_RefreshesPermissionsBeforeTheyLapse()
    {
        var time = new FakeTimeProvider();
        await using var server = new TestTurnServer(IPAddress.Loopback);
        using TurnAllocation allocation = await Allocate(server, TurnTransport.Udp, time);
        _ = Pump(allocation);
        using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        await allocation.SendAsync("x"u8.ToArray(), (IPEndPoint)peer.LocalEndPoint!, default);
        _ = await peer.ReceiveFromAsync(
                new byte[64],
                SocketFlags.None,
                new IPEndPoint(IPAddress.Any, 0)
            )
            .WaitAsync(TimeSpan.FromSeconds(5));

        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromMinutes(4));
        while (server.CreatePermissions < 2)
        {
            // Deliberate: lets the refresh reach the server before checking again.
            await Task.Delay(10, guard.Token);
        }

        Assert.AreEqual(2, server.CreatePermissions);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ChannelBind_StaleNonce_RetriesWithTheFreshOne()
    {
        await using var server = new TestTurnServer(IPAddress.Loopback, staleNonces: 1);
        using TurnAllocation allocation = await Allocate(
            server,
            TurnTransport.Udp,
            TimeProvider.System
        );
        _ = Pump(allocation);
        var peer = new IPEndPoint(IPAddress.Loopback, 9);

        await allocation.SendAsync("x"u8.ToArray(), peer, default);
        await server.WhenChannelBound.WaitAsync(TimeSpan.FromSeconds(5));

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

        // The server answering the bind does not mean the allocation has recorded it; a datagram arriving
        // as ChannelData does.
        while (!server.WhenChannelData.IsCompleted)
        {
            await allocation.SendAsync("x"u8.ToArray(), peer, default);

            // Deliberate: retry pacing; sends repeat until the bind has taken effect, as ICE checks do.
            await Task.WhenAny(server.WhenChannelData, Task.Delay(20));
        }

        // The binding is refreshed on the maintenance tick at four minutes and the 600 s allocation on the
        // first tick past 7.5. The clock stops on each tick that starts a request and waits for the
        // server to have it, so no request times out while the clock moves on.
        time.Advance(TimeSpan.FromMinutes(4));
        await server.WhenRefreshedBinding.WaitAsync(TimeSpan.FromSeconds(5));

        // A tick is skipped while the previous pass is still reading the server's answer, so the clock
        // keeps moving a tick at a time until the allocation refresh reaches the server.
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(5));
        while (!server.WhenRefreshed.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(30));

            // Deliberate: lets the allocation finish reading the answer before the clock moves again.
            await Task.WhenAny(server.WhenRefreshed, Task.Delay(10, guard.Token));
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
        await server.WhenDeallocated.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, server.Deallocations);
    }

    // RFC 8656 section 7.4: once three client ports met a 437, the server is left alone for two
    // minutes, across ICE restarts.
    [TestMethod]
    [Timeout(20_000)]
    public async Task Agent_AllocationMismatchOnEveryPort_LeavesTheServerAloneForTwoMinutes()
    {
        var time = new FakeTimeProvider();
        await using var server = new TestTurnServer(IPAddress.Loopback, mismatches: int.MaxValue);
        var turn = new TurnServer(
            "127.0.0.1",
            server.UdpEndPoint.Port,
            TurnTransport.Udp,
            TestTurnServer.Username,
            TestTurnServer.Password
        );
        await using var agent = new IceAgent(
            IceCredentials.Generate(),
            IceRole.Controlling,
            timeProvider: time,
            transportPolicy: IceTransportPolicy.Relay
        );
        agent.AddTurnServer(turn);
        agent.SetRemoteCredentials(IceCredentials.Generate());

        agent.Start();
        await agent.WhenGatheredAsync(TimeSpan.FromMinutes(1)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(
            6,
            server.AllocateRequests,
            "three ports, each challenged and then refused"
        );

        time.Advance(TimeSpan.FromSeconds(119));
        agent.Restart(IceCredentials.Generate());
        await agent.WhenGatheredAsync(TimeSpan.FromMinutes(1)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(6, server.AllocateRequests, "held off within the two minutes");

        time.Advance(TimeSpan.FromSeconds(1));
        agent.Restart(IceCredentials.Generate());
        await agent.WhenGatheredAsync(TimeSpan.FromMinutes(1)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(12, server.AllocateRequests, "asked again after them");
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
        TaskCompletionSource<byte[]> data = new(TaskCreationOptions.RunContinuationsAsynchronously);
        answerer.DataReceived += (in IceDatagram datagram) =>
            data.TrySetResult(datagram.Data.Span[1..].ToArray());

        offerer.Start();
        answerer.Start();
        await connected.Task;
        for (int attempt = 0; attempt < 100 && !data.Task.IsCompleted; attempt++)
        {
            // The first byte puts the datagram in the RTP range of the demultiplexer.
            // Deliberate: sends repeat until the relay accepts one, as ICE checks do; nothing signals
            // that the answerer's relay has permitted the offerer's address.
            await offerer.SendAsync((byte[])[0x80, .. "media"u8]);
            await Task.WhenAny(data.Task, Task.Delay(50));
        }

        CollectionAssert.AreEqual("media"u8.ToArray(), await data.Task);
        lock (gathered)
        {
            Assert.IsTrue(gathered.TrueForAll(static c => c.Kind == IceCandidateKind.Relayed));
            Assert.IsNotEmpty(gathered);
        }

        Assert.IsGreaterThan(0, server.ChannelDataFrames);
    }

    [TestMethod]
    [DataRow("turn:turn.example.com", "turn.example.com", 3478, TurnTransport.Udp)]
    [DataRow(
        "turn:turn.example.com:3479?transport=tcp",
        "turn.example.com",
        3479,
        TurnTransport.Tcp
    )]
    [DataRow("turns:turn.example.com", "turn.example.com", 5349, TurnTransport.Tls)]
    [DataRow(
        "turns:turn.example.com:443?transport=tcp",
        "turn.example.com",
        443,
        TurnTransport.Tls
    )]
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

    // RFC 8489 section 9.2.5: a server listing password algorithms gets the first one this side has
    // (SHA-256), echoed with the list, and MESSAGE-INTEGRITY-SHA256 on every request after; with the
    // username anonymity bit, USERHASH in place of USERNAME. The refresh, permission and bind all pass.
    [TestMethod]
    [Timeout(10_000)]
    [DataRow(TurnSecurity.Sha256, false)]
    [DataRow(TurnSecurity.Sha256Anonymous, true)]
    public async Task PasswordAlgorithms_AuthenticateWithSha256(
        TurnSecurity security,
        bool userhash
    )
    {
        await using var server = new TestTurnServer(IPAddress.Loopback, security: security);
        using TurnAllocation allocation = await Allocate(
            server,
            TurnTransport.Udp,
            TimeProvider.System
        );
        _ = Pump(allocation);

        await allocation.SendAsync("x"u8.ToArray(), new IPEndPoint(IPAddress.Loopback, 9), default);
        await server.WhenChannelBound.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsGreaterThanOrEqualTo(2, server.Sha256Requests, "Allocate and ChannelBind");
        Assert.AreEqual(userhash ? server.Sha256Requests : 0, server.UserhashRequests);
    }

    // Section 9.2.5: a cookie announcing password algorithms with no list means it was stripped on the
    // way (section 9.2.1), and a list with nothing this side has leaves no key to use; neither is answered.
    [TestMethod]
    [Timeout(10_000)]
    [DataRow(TurnSecurity.StrippedAlgorithms)]
    [DataRow(TurnSecurity.UnknownAlgorithm)]
    public async Task PasswordAlgorithms_UnusableChallenge_IsNotAnswered(TurnSecurity security)
    {
        await using var server = new TestTurnServer(IPAddress.Loopback, security: security);

        TurnException refused = await Assert.ThrowsExactlyAsync<TurnException>(() =>
            Allocate(server, TurnTransport.Udp, TimeProvider.System)
        );

        Assert.AreEqual(401, refused.Code);
        Assert.AreEqual(1, server.AllocateRequests, "no retry with credentials");
    }

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
                    IceReceiveResult result = await allocation.ReceiveAsync(
                        buffer,
                        CancellationToken.None
                    );
                    received.Writer.TryWrite((buffer[..result.Length], result.RemoteEndPoint));
                }
            }
            catch (Exception exception)
                when (exception
                        is ObjectDisposedException
                            or SocketException
                            or IOException
                            or OperationCanceledException
                )
            {
                received.Writer.TryComplete(exception is IOException ? exception : null);
            }
        });
        return received;
    }
}
