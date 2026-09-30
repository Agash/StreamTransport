using System.Collections.Concurrent;
using System.Net;
using Agash.StreamTransport.WebRtc.Ice;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// ICE candidate switching and failover over an <see cref="InMemoryIceNetwork"/> on simulated time: two
/// agents with two interfaces each connect, then the selected path is cut and the agent must switch to
/// its pre-warmed alternate (hot-standby) and keep delivering data. The agents run the production RFC
/// timings; simulated time makes a 30 s consent timeout take a moment.
/// </summary>
[TestClass]
public sealed class IceMobilityTests
{
    private static readonly IceTimings Timings = IceTimings.Default;

    [TestMethod]
    [Timeout(60_000)]
    public async Task TwoAgents_OverInMemoryNetwork_ConnectAndExchangeData()
    {
        SimulatedTime time = new();
        var net = new InMemoryIceNetwork();
        (IceAgent a, IceAgent b) = await ConnectPairAsync(
            time,
            net,
            aAddrs: [IPAddress.Parse("10.0.0.1")],
            bAddrs: [IPAddress.Parse("10.0.0.2")]
        );

        await using (a)
        await using (b)
        {
            var received = new TaskCompletionSource<byte[]>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            b.DataReceived += (data, _, _) => received.TrySetResult(data.ToArray());

            byte[] payload = [0x10, 0x20, 0x30, 0x40]; // non-STUN -> surfaced as data.
            await a.SendAsync(payload);

            byte[] got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(payload, got);
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Controlled_NominationBeforeOwnCheckSucceeds_SelectsPairOnceItDoes()
    {
        SimulatedTime time = new();
        var net = new InMemoryIceNetwork();
        var controllingAddress = IPAddress.Parse("10.0.0.1");

        // Hold the controlling agent's binding success responses: its own checks succeed and it
        // nominates while every check of the controlled agent is still unanswered.
        net.Hold(
            (from, data) => from.Address.Equals(controllingAddress) && data is [0x01, 0x01, ..]
        );
        (IceAgent a, IceAgent b, Task aConnected, Task bConnected) = CreatePair(
            time,
            net,
            [controllingAddress],
            [IPAddress.Parse("10.0.0.2")]
        );

        await using (a)
        await using (b)
        {
            Assert.IsTrue(await time.RunUntilAsync(aConnected, TimeSpan.FromSeconds(10)));
            Assert.IsFalse(bConnected.IsCompleted);

            net.ReleaseHeld();

            Assert.IsTrue(await time.RunUntilAsync(bConnected, TimeSpan.FromSeconds(10)));
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task HotStandby_SwitchesToWarmPair_WhenSelectedPathIsCut()
    {
        SimulatedTime time = new();
        var net = new InMemoryIceNetwork();
        (IceAgent a, IceAgent b) = await ConnectPairAsync(
            time,
            net,
            aAddrs: [IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.1.0.1")],
            bAddrs: [IPAddress.Parse("10.0.0.2"), IPAddress.Parse("10.1.0.2")]
        );

        await using (a)
        await using (b)
        {
            var received = new ConcurrentQueue<byte[]>();
            b.DataReceived += (data, _, _) => received.Enqueue(data.ToArray());

            IPAddress original = a.SelectedLocalEndpoint!.Address;
            bool stayedConnected = true;
            a.StateChanged += s => stayedConnected &= s == IceConnectionState.Connected;

            // Cut the selected path. The other interface's pairs stay warm (hot-standby keep-alive), so
            // the agent switches to one of them once consent on the cut path times out.
            net.Cut(original);

            bool switched = await time.RunUntilAsync(
                () => a.SelectedLocalEndpoint is { } sel && !sel.Address.Equals(original),
                Timings.ConsentTimeout * 2
            );

            Assert.IsTrue(
                switched,
                $"the agent should switch off the cut path {original}; still on {a.SelectedLocalEndpoint?.Address}."
            );
            Assert.IsTrue(stayedConnected, "it must stay Connected across the switch.");

            byte[] payload = [0x55, 0x66, 0x77, 0x88];
            await a.SendAsync(payload);
            Assert.IsTrue(
                await time.RunUntilAsync(() => !received.IsEmpty, TimeSpan.FromSeconds(1)),
                "data must flow again over the warm alternate after the switch."
            );
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task FlakyLink_StaysConnectedAndDeliversData_UnderProbabilisticLoss()
    {
        SimulatedTime time = new();
        var net = new InMemoryIceNetwork();
        (IceAgent a, IceAgent b) = await ConnectPairAsync(
            time,
            net,
            aAddrs: [IPAddress.Parse("10.0.0.1")],
            bAddrs: [IPAddress.Parse("10.0.0.2")]
        );

        await using (a)
        await using (b)
        {
            int delivered = 0;
            b.DataReceived += (_, _, _) => Interlocked.Increment(ref delivered);

            // Degrade the single path to 30% loss (a weak signal) without cutting it. There is no alternate
            // interface, so the agent cannot switch away - it must ride out the loss for two consent timeouts.
            net.SetLossRate(0.30);

            byte[] payload = [0x10, 0x20, 0x30, 0x40];
            int sent = 0;
            for (
                TimeSpan elapsed = TimeSpan.Zero;
                elapsed < Timings.ConsentTimeout * 2;
                elapsed += time.Step
            )
            {
                await a.SendAsync(payload);
                sent++;
                await time.RunAsync(time.Step);
            }

            // Consent checks in both directions refresh it, so sporadic loss never lets it lapse, and a
            // clear majority of datagrams still arrives.
            Assert.AreEqual(
                IceConnectionState.Connected,
                a.State,
                "a flaky (degraded, not cut) link must stay connected."
            );
            Assert.IsGreaterThan(
                sent * 0.6,
                delivered,
                $"most datagrams should cross a 30% loss link; got {delivered}/{sent}."
            );
        }
    }

    private static async Task<(IceAgent A, IceAgent B)> ConnectPairAsync(
        SimulatedTime time,
        InMemoryIceNetwork net,
        IPAddress[] aAddrs,
        IPAddress[] bAddrs
    )
    {
        (IceAgent a, IceAgent b, Task aConnected, Task bConnected) = CreatePair(
            time,
            net,
            aAddrs,
            bAddrs
        );

        Assert.IsTrue(
            await time.RunUntilAsync(
                Task.WhenAll(aConnected, bConnected),
                TimeSpan.FromSeconds(10)
            ),
            "the agents connect within ten seconds of simulated time"
        );

        // Let hot-standby keep-alives validate the alternate pairs before any failover test.
        await time.RunAsync(Timings.ConsentInterval * 2);
        return (a, b);
    }

    // Starts a controlling agent A and a controlled agent B that trickle candidates to each other.
    private static (IceAgent A, IceAgent B, Task AConnected, Task BConnected) CreatePair(
        SimulatedTime time,
        InMemoryIceNetwork net,
        IPAddress[] aAddrs,
        IPAddress[] bAddrs
    )
    {
        var credsA = IceCredentials.Generate();
        var credsB = IceCredentials.Generate();
        var a = new IceAgent(
            credsA,
            IceRole.Controlling,
            includeLoopback: true,
            socketFactory: net.Factory(aAddrs),
            timings: Timings,
            timeProvider: time.Clock
        );
        var b = new IceAgent(
            credsB,
            IceRole.Controlled,
            includeLoopback: true,
            socketFactory: net.Factory(bAddrs),
            timings: Timings,
            timeProvider: time.Clock
        );

        a.LocalCandidateGathered += c => b.AddRemoteCandidate(c);
        b.LocalCandidateGathered += c => a.AddRemoteCandidate(c);
        a.SetRemoteCredentials(credsB);
        b.SetRemoteCredentials(credsA);

        Task aConnected = Connected(a);
        Task bConnected = Connected(b);
        a.Start();
        b.Start();
        return (a, b, aConnected, bConnected);
    }

    private static Task Connected(IceAgent agent)
    {
        var connected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        agent.StateChanged += s =>
        {
            if (s == IceConnectionState.Connected)
            {
                connected.TrySetResult();
            }
        };
        return connected.Task;
    }
}
