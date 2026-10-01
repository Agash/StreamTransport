using System.Net;
using System.Threading.Channels;
using Agash.StreamTransport.WebRtc.Ice;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// The agent as a driver of its state machine: sockets, receive loops and its timer, over an
/// <see cref="InMemoryIceNetwork"/> on the system clock. The protocol itself is covered by
/// <see cref="IceMobilityTests"/>.
/// </summary>
[TestClass]
public sealed class IceAgentTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [TestMethod]
    [Timeout(30_000)]
    public async Task TwoAgents_Connect_AndCarryDataBothWays()
    {
        await using var agents = Agents.Start();
        await agents.BothConnectedAsync();

        byte[] payload = [0x10, 0x20, 0x30, 0x40]; // Not STUN, so surfaced as data.
        await agents.A.SendAsync(payload);
        CollectionAssert.AreEqual(
            payload,
            await agents.ReceivedByB.ReadAsync().AsTask().WaitAsync(Patience)
        );

        await agents.B.SendAsync(payload);
        CollectionAssert.AreEqual(
            payload,
            await agents.ReceivedByA.ReadAsync().AsTask().WaitAsync(Patience)
        );
        Assert.IsNotNull(agents.A.SelectedPath);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Connected_StaysConnectedPastManyConsentTimeouts()
    {
        IceTimings quick = IceTimings.Default with
        {
            ConsentInterval = TimeSpan.FromMilliseconds(100),
            ConsentTimeout = TimeSpan.FromMilliseconds(600),
        };
        await using var agents = Agents.Start(quick);
        await agents.BothConnectedAsync();
        IcePath path = agents.A.SelectedPath!.Value;
        TaskCompletionSource left = new(TaskCreationOptions.RunContinuationsAsynchronously);
        agents.A.StateChanged += s => left.TrySetResult();
        agents.B.StateChanged += s => left.TrySetResult();

        // Five consent timeouts: a side whose checks went unanswered would have changed state by now.
        await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            left.Task.WaitAsync(quick.ConsentTimeout * 5)
        );
        Assert.AreEqual(path, agents.A.SelectedPath);
        Assert.AreEqual(IceConnectionState.Connected, agents.B.State);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DataHandlerThatThrows_LosesThePacketNotTheSocket()
    {
        await using var agents = Agents.Start();
        await agents.BothConnectedAsync();
        int calls = 0;
        agents.B.DataReceived += (_, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("A malformed packet.");
            }
        };

        await agents.A.SendAsync(new byte[] { 0x10 });
        await agents.ReceivedByB.ReadAsync().AsTask().WaitAsync(Patience);
        byte[] second = [0x20];
        await agents.A.SendAsync(second);

        CollectionAssert.AreEqual(
            second,
            await agents.ReceivedByB.ReadAsync().AsTask().WaitAsync(Patience)
        );
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DataReceived_FromSeveralSockets_IsRaisedOnePacketAtATime()
    {
        InMemoryIceNetwork network = new();
        IPAddress[] addresses = [IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.1.1")];
        await using IceAgent agent = new(
            IceCredentials.Generate(),
            IceRole.Controlled,
            socketFactory: network.Factory(addresses)
        );
        const int perSocket = 100;
        int inside = 0;
        int overlapped = 0;
        int seen = 0;
        TaskCompletionSource all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.DataReceived += (_, _, _) =>
        {
            if (Interlocked.Increment(ref inside) > 1)
            {
                Interlocked.Increment(ref overlapped);
            }

            Thread.SpinWait(20_000);
            Interlocked.Decrement(ref inside);
            if (Interlocked.Increment(ref seen) == perSocket * addresses.Length)
            {
                all.TrySetResult();
            }
        };
        agent.Start();

        // Both sockets carry packets at once, as the old path and a warm standby do around a switch.
        IPEndPoint peer = new(IPAddress.Parse("10.9.9.9"), 9);
        IPEndPoint[] sockets = [.. addresses.SelectMany(network.BoundOn)];
        Assert.HasCount(addresses.Length, sockets);
        foreach (IPEndPoint socket in sockets)
        {
            for (int i = 0; i < perSocket; i++)
            {
                network.Inject(peer, socket, [0x10]);
            }
        }

        await all.Task.WaitAsync(Patience);
        Assert.AreEqual(0, overlapped);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Restart_RebindsAndReconnectsUnderNewCredentials()
    {
        await using var agents = Agents.Start();
        await agents.BothConnectedAsync();
        IPEndPoint before = agents.A.SelectedPath!.Value.Local;

        var fresh = IceCredentials.Generate();
        agents.A.Restart(fresh);
        agents.B.SetRemoteCredentials(fresh);
        await agents.AConnected.ReadAsync().AsTask().WaitAsync(Patience);

        Assert.AreEqual(fresh, agents.A.LocalCredentials);
        Assert.AreNotEqual(before, agents.A.SelectedPath?.Local, "the restart bound new sockets.");
        byte[] payload = [0x11];
        await agents.A.SendAsync(payload);
        CollectionAssert.AreEqual(
            payload,
            await agents.ReceivedByB.ReadAsync().AsTask().WaitAsync(Patience)
        );
    }

    [TestMethod]
    public async Task Send_BeforeAPairIsSelected_IsDropped()
    {
        await using IceAgent agent = new(
            IceCredentials.Generate(),
            IceRole.Controlling,
            socketFactory: new InMemoryIceNetwork().Factory(IPAddress.Parse("10.0.0.1"))
        );

        await agent.SendAsync(new byte[] { 0x10 });
        Assert.IsNull(agent.SelectedPath);
        Assert.AreEqual(IceConnectionState.New, agent.State);
    }

    // A controlling agent A and a controlled agent B trickling to each other, recording what they report.
    private sealed class Agents : IAsyncDisposable
    {
        private readonly Channel<byte[]> _receivedByA = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<byte[]> _receivedByB = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<bool> _aConnected = Channel.CreateUnbounded<bool>();
        private readonly Channel<bool> _bConnected = Channel.CreateUnbounded<bool>();

        private Agents(IceAgent a, IceAgent b)
        {
            A = a;
            B = b;
        }

        public IceAgent A { get; }

        public IceAgent B { get; }

        public ChannelReader<byte[]> ReceivedByA => _receivedByA.Reader;

        public ChannelReader<byte[]> ReceivedByB => _receivedByB.Reader;

        public ChannelReader<bool> AConnected => _aConnected.Reader;

        public static Agents Start(IceTimings? timings = null)
        {
            InMemoryIceNetwork network = new();
            var credentialsA = IceCredentials.Generate();
            var credentialsB = IceCredentials.Generate();
            Agents agents = new(
                new IceAgent(
                    credentialsA,
                    IceRole.Controlling,
                    socketFactory: network.Factory(IPAddress.Parse("10.0.0.1")),
                    timings: timings
                ),
                new IceAgent(
                    credentialsB,
                    IceRole.Controlled,
                    socketFactory: network.Factory(IPAddress.Parse("10.0.0.2"))
                )
            );
            agents.A.LocalCandidateGathered += agents.B.AddRemoteCandidate;
            agents.B.LocalCandidateGathered += agents.A.AddRemoteCandidate;
            agents.A.SetRemoteCredentials(credentialsB);
            agents.B.SetRemoteCredentials(credentialsA);
            agents.A.DataReceived += (data, _, _) =>
                agents._receivedByA.Writer.TryWrite(data.ToArray());
            agents.B.DataReceived += (data, _, _) =>
                agents._receivedByB.Writer.TryWrite(data.ToArray());
            agents.A.StateChanged += s => Record(agents._aConnected, s);
            agents.B.StateChanged += s => Record(agents._bConnected, s);
            agents.A.Start();
            agents.B.Start();
            return agents;
        }

        public async Task BothConnectedAsync()
        {
            await _aConnected.Reader.ReadAsync().AsTask().WaitAsync(Patience);
            await _bConnected.Reader.ReadAsync().AsTask().WaitAsync(Patience);
        }

        public async ValueTask DisposeAsync()
        {
            await A.DisposeAsync();
            await B.DisposeAsync();
        }

        private static void Record(Channel<bool> connected, IceConnectionState state)
        {
            if (state == IceConnectionState.Connected)
            {
                _ = connected.Writer.TryWrite(true);
            }
        }
    }
}
