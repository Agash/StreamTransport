using System.Collections.Concurrent;
using System.Net;
using Agash.StreamTransport.WebRtc.Ice;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// ICE candidate switching / failover, tested deterministically over an <see cref="InMemoryIceNetwork"/> (no
/// real sockets): two agents with two interfaces each connect, then the selected path is cut and the agent
/// must switch to its pre-warmed alternate (hot-standby) and keep delivering data - validating
/// warmed-candidate failover. Fast <see cref="IceTimings"/> make consent loss fire in well under a second.
/// </summary>
[TestClass]
public sealed class IceMobilityTests
{
    private static readonly IceTimings Fast = new(
        TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(500));

    [TestMethod]
    [Timeout(40_000)]
    public async Task TwoAgents_OverInMemoryNetwork_ConnectAndExchangeData()
    {
        var net = new InMemoryIceNetwork();
        (IceAgent a, IceAgent b) = await ConnectPairAsync(net,
            aAddrs: [IPAddress.Parse("10.0.0.1")], bAddrs: [IPAddress.Parse("10.0.0.2")]);

        await using (a)
        await using (b)
        {
            var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            b.DataReceived += (data, _, _) => received.TrySetResult(data.ToArray());

            byte[] payload = [0x10, 0x20, 0x30, 0x40]; // non-STUN -> surfaced as data.
            await a.SendAsync(payload);

            byte[] got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(payload, got);
        }
    }

    [TestMethod]
    [Timeout(40_000)]
    public async Task HotStandby_SwitchesToWarmPair_WhenSelectedPathIsCut()
    {
        var net = new InMemoryIceNetwork();
        (IceAgent a, IceAgent b) = await ConnectPairAsync(net,
            aAddrs: [IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.1.0.1")],
            bAddrs: [IPAddress.Parse("10.0.0.2"), IPAddress.Parse("10.1.0.2")]);

        await using (a)
        await using (b)
        {
            var received = new ConcurrentQueue<byte[]>();
            b.DataReceived += (data, _, _) => received.Enqueue(data.ToArray());

            IPAddress original = a.SelectedLocalEndpoint!.Address;

            // Cut the selected path. The other interface's pairs stay warm (hot-standby keep-alive), so the
            // agent should switch to one of them within ~a consent timeout, never leaving Connected.
            net.Cut(original);

            // 1) The selected pair switches away from the dead path.
            bool switched = false;
            for (int i = 0; i < 300 && !switched; i++)
            {
                switched = a.SelectedLocalEndpoint is { } sel && !sel.Address.Equals(original);
                if (!switched)
                {
                    await Task.Delay(25);
                }
            }

            Assert.IsTrue(switched, $"the agent should switch off the cut path {original}; still on {a.SelectedLocalEndpoint?.Address}.");
            Assert.AreEqual(IceConnectionState.Connected, a.State, "it must stay Connected across the switch.");

            // 2) Media flows again over the new path.
            byte[] payload = [0x55, 0x66, 0x77, 0x88];
            for (int i = 0; i < 100 && received.IsEmpty; i++)
            {
                await a.SendAsync(payload);
                await Task.Delay(25);
            }

            Assert.IsFalse(received.IsEmpty, "data must flow again over the warm alternate after the switch.");
        }
    }

    [TestMethod]
    [Timeout(40_000)]
    public async Task FlakyLink_StaysConnectedAndDeliversData_UnderProbabilisticLoss()
    {
        var net = new InMemoryIceNetwork();
        (IceAgent a, IceAgent b) = await ConnectPairAsync(net,
            aAddrs: [IPAddress.Parse("10.0.0.1")], bAddrs: [IPAddress.Parse("10.0.0.2")]);

        await using (a)
        await using (b)
        {
            int delivered = 0;
            b.DataReceived += (_, _, _) => Interlocked.Increment(ref delivered);

            // Degrade the single path to 30% loss (a weak signal) without cutting it. There is no alternate
            // interface, so the agent cannot switch away - it must ride out the loss on the one path.
            net.SetLossRate(0.30);

            const int sent = 200;
            byte[] payload = [0x10, 0x20, 0x30, 0x40];
            for (int i = 0; i < sent; i++)
            {
                await a.SendAsync(payload);
                await Task.Delay(5);
            }

            // Consent freshness retries (many checks per timeout window) survive sporadic loss, so the
            // connection must stay up, and a clear majority of datagrams must still arrive.
            Assert.AreEqual(IceConnectionState.Connected, a.State, "a flaky (degraded, not cut) link must stay connected.");
            Assert.IsTrue(delivered > sent * 0.4, $"most datagrams should still cross a 30% loss link; got {delivered}/{sent}.");
        }
    }

    private static async Task<(IceAgent A, IceAgent B)> ConnectPairAsync(
        InMemoryIceNetwork net, IPAddress[] aAddrs, IPAddress[] bAddrs)
    {
        var credsA = IceCredentials.Generate();
        var credsB = IceCredentials.Generate();
        var a = new IceAgent(credsA, IceRole.Controlling, includeLoopback: true, socketFactory: net.Factory(aAddrs), timings: Fast);
        var b = new IceAgent(credsB, IceRole.Controlled, includeLoopback: true, socketFactory: net.Factory(bAddrs), timings: Fast);

        a.LocalCandidateGathered += c => b.AddRemoteCandidate(c);
        b.LocalCandidateGathered += c => a.AddRemoteCandidate(c);
        a.SetRemoteCredentials(credsB);
        b.SetRemoteCredentials(credsA);

        var aConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.StateChanged += s => { if (s == IceConnectionState.Connected) { aConnected.TrySetResult(); } };
        b.StateChanged += s => { if (s == IceConnectionState.Connected) { bConnected.TrySetResult(); } };

        a.Start();
        b.Start();

        // Generous: under full-suite CPU contention the 20 ms check cadence stretches, but connect is quick.
        await Task.WhenAll(aConnected.Task, bConnected.Task).WaitAsync(TimeSpan.FromSeconds(25));

        // Let hot-standby keep-alives validate the alternate pairs before any failover test.
        await Task.Delay(250);
        return (a, b);
    }
}
