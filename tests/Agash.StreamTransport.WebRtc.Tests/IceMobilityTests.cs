using System.Net;
using System.Net.Sockets;
using Agash.StreamTransport.WebRtc.Ice;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// ICE connection, candidate switching and failover, with two state machines on an
/// <see cref="IceSwitchboard"/>. The machines run the production RFC timings; the switchboard jumps time
/// to each timeout, so a 30 s consent timeout runs instantly and the same way every time.
/// </summary>
[TestClass]
public sealed class IceMobilityTests
{
    private static readonly IceTimings Timings = IceTimings.Default;

    [TestMethod]
    public void TwoMachines_Connect_AndSelectAPairEachWay()
    {
        IceSwitchboard board = new();
        (IceSwitchboard.Peer a, IceSwitchboard.Peer b) = Connect(
            board,
            [IPAddress.Parse("10.0.0.1")],
            [IPAddress.Parse("10.0.0.2")]
        );

        Assert.AreEqual(b.Endpoints[0], a.Machine.Selected?.Remote);
        Assert.AreEqual(a.Endpoints[0], b.Machine.Selected?.Remote);
        Assert.IsTrue(board.SendData(a));
        Assert.IsTrue(board.SendData(b));
    }

    [TestMethod]
    public void Start_AsksOnlyTheStunServersEachAddressReaches()
    {
        IceStateMachine machine = new(
            IceCredentials.Generate(),
            IceRole.Controlling,
            Timings,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance
        );
        machine.AddLocalEndpoint(0, new IPEndPoint(IPAddress.Parse("2001:db8::1"), 5000));
        machine.AddLocalEndpoint(1, new IPEndPoint(IPAddress.Parse("192.0.2.1"), 5000));
        machine.AddLocalEndpoint(2, new IPEndPoint(IPAddress.Loopback, 5000));
        machine.AddStunServer(new IPEndPoint(IPAddress.IPv6Loopback, 3478));
        machine.AddStunServer(new IPEndPoint(IPAddress.Loopback, 3478));
        machine.AddStunServer(new IPEndPoint(IPAddress.Parse("2001:db8::99"), 3478));

        machine.Start(TimeSpan.Zero);

        List<(int, IPEndPoint)> sent = [];
        while (machine.TryPollTransmit(out IceTransmit transmit))
        {
            sent.Add((transmit.Local, transmit.Destination));
        }

        CollectionAssert.AreEquivalent(
            new List<(int, IPEndPoint)>
            {
                (0, new IPEndPoint(IPAddress.Parse("2001:db8::99"), 3478)),
                (2, new IPEndPoint(IPAddress.Loopback, 3478)),
            },
            sent
        );
    }

    [TestMethod]
    public void Controlled_NominationBeforeOwnCheckSucceeds_SelectsPairOnceItDoes()
    {
        IceSwitchboard board = new();
        var controlling = IPAddress.Parse("10.0.0.1");

        // Hold the controlling machine's binding success responses: its own checks succeed and it
        // nominates while every check of the controlled machine is still unanswered.
        board.Hold((from, data) => from.Address.Equals(controlling) && data is [0x01, 0x01, ..]);
        (IceSwitchboard.Peer a, IceSwitchboard.Peer b) = board.Pair(
            [controlling],
            [IPAddress.Parse("10.0.0.2")],
            Timings
        );

        Assert.IsTrue(board.RunUntil(() => a.Connected, TimeSpan.FromSeconds(10)));
        Assert.IsFalse(b.Connected);

        board.ReleaseHeld();

        Assert.IsTrue(board.RunUntil(() => b.Connected, TimeSpan.FromSeconds(10)));
    }

    [TestMethod]
    public void HotStandby_SwitchesToWarmPair_WhenSelectedPathIsCut()
    {
        IceSwitchboard board = new();
        (IceSwitchboard.Peer a, IceSwitchboard.Peer b) = Connect(
            board,
            [IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.1.0.1")],
            [IPAddress.Parse("10.0.0.2"), IPAddress.Parse("10.1.0.2")]
        );
        IPAddress original = a.SelectedAddress!;
        int statesBefore = a.States.Count;

        // Cut the selected path. The other interface's pairs stay warm (hot-standby keep-alive), so the
        // machine switches to one of them once consent on the cut path times out.
        board.Cut(original);

        Assert.IsTrue(
            board.RunUntil(
                () => a.SelectedAddress is { } now && !now.Equals(original),
                Timings.ConsentTimeout * 2
            ),
            $"the machine should switch off the cut path {original}; still on {a.SelectedAddress}."
        );
        Assert.IsTrue(
            a.States.Skip(statesBefore).All(s => s == IceConnectionState.Connected),
            $"it stays Connected across the switch: {string.Join(", ", a.States.Skip(statesBefore))}."
        );
        Assert.IsTrue(board.SendData(a), "data flows over the warm alternate after the switch.");
        Assert.IsTrue(board.RunUntil(() => board.SendData(b), Timings.ConsentTimeout * 2));
    }

    [TestMethod]
    public void FlakyLink_StaysConnectedAndDeliversData_UnderProbabilisticLoss()
    {
        IceSwitchboard board = new();
        (IceSwitchboard.Peer a, _) = Connect(
            board,
            [IPAddress.Parse("10.0.0.1")],
            [IPAddress.Parse("10.0.0.2")]
        );

        // Degrade the only path to 30% loss without cutting it. There is no alternate to switch to, so
        // the machine rides out the loss for two consent timeouts.
        board.SetLossRate(0.30);
        var step = TimeSpan.FromMilliseconds(50);
        int sent = 0;
        int delivered = 0;
        for (
            TimeSpan elapsed = TimeSpan.Zero;
            elapsed < Timings.ConsentTimeout * 2;
            elapsed += step
        )
        {
            sent++;
            delivered += board.SendData(a) ? 1 : 0;
            board.Run(step);
        }

        // Consent checks in both directions refresh it, so sporadic loss never lets it lapse, and a
        // clear majority of datagrams still arrives.
        Assert.AreEqual(IceConnectionState.Connected, a.Machine.State);
        Assert.IsGreaterThan(
            sent * 0.6,
            delivered,
            $"most datagrams cross a 30% loss link; got {delivered}/{sent}."
        );
    }

    [TestMethod]
    public void ConsentLost_WithNoAlternate_RecoversWhenThePathReturns()
    {
        IceSwitchboard board = new();
        var address = IPAddress.Parse("10.0.0.1");
        (IceSwitchboard.Peer a, _) = Connect(board, [address], [IPAddress.Parse("10.0.0.2")]);

        board.Cut(address);
        Assert.IsTrue(
            board.RunUntil(() => !a.Connected, Timings.ConsentTimeout * 2),
            "consent lapses on a dead path."
        );

        board.Uncut(address);
        Assert.IsTrue(
            board.RunUntil(() => a.Connected, TimeSpan.FromSeconds(10)),
            "the machine reconnects once the path is back."
        );
        Assert.IsTrue(board.SendData(a));
    }

    [TestMethod]
    public void Restart_UnderNewCredentials_Reconnects()
    {
        IceSwitchboard board = new();
        (IceSwitchboard.Peer a, IceSwitchboard.Peer b) = Connect(
            board,
            [IPAddress.Parse("10.0.0.1")],
            [IPAddress.Parse("10.0.0.2")]
        );

        var fresh = IceCredentials.Generate();
        board.Restart(a, fresh, [IPAddress.Parse("10.0.0.1")]);
        b.Machine.SetRemoteCredentials(fresh);

        Assert.IsTrue(board.RunUntil(() => a.Connected && b.Connected, TimeSpan.FromSeconds(10)));
        Assert.AreEqual(fresh, a.Machine.LocalCredentials);
        Assert.IsTrue(board.SendData(a));
    }

    [TestMethod]
    public void Timeouts_AreNotDueEarly_AndStepOncePerCall()
    {
        IceSwitchboard board = new();
        (IceSwitchboard.Peer a, _) = board.Pair(
            [IPAddress.Parse("10.0.0.1")],
            [IPAddress.Parse("10.0.0.2")],
            Timings
        );
        TimeSpan due = a.Machine.NextTimeout!.Value;

        a.Machine.HandleTimeout(due - TimeSpan.FromTicks(1));
        Assert.AreEqual(due, a.Machine.NextTimeout, "an early call changes nothing.");

        // A driver that fell a long way behind gets one step, then paces at Ta again.
        TimeSpan late = due + TimeSpan.FromSeconds(3);
        a.Machine.HandleTimeout(late);
        Assert.AreEqual(late + Timings.Ta, a.Machine.NextTimeout);
    }

    [TestMethod]
    public void NetworkEvent_KeepsMediaOnTheSelectedPair_WhileEveryPairIsRechecked()
    {
        IceSwitchboard board = new();
        (IceSwitchboard.Peer a, IceSwitchboard.Peer b) = Connect(
            board,
            [IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.1.0.1")],
            [IPAddress.Parse("10.0.0.2"), IPAddress.Parse("10.1.0.2")]
        );
        var selected = a.Machine.Selected;
        int statesBefore = a.States.Count;

        // An address change that left the path working, such as an IPv6 privacy address rotating.
        a.Machine.TriggerRecovery();
        (int sent, int fromA, int fromB) = board.Stream(TimeSpan.FromSeconds(3));

        Assert.AreEqual(sent, fromA, "every datagram is sent and arrives across the re-check.");
        Assert.AreEqual(sent, fromB);
        Assert.AreEqual(selected, a.Machine.Selected);
        Assert.AreEqual(statesBefore, a.States.Count, "the state never leaves Connected.");
        Assert.IsTrue(b.Connected);
    }

    [TestMethod]
    public void SilentPathDeath_FailsOverWithinSeconds_AndTheControlledSideFollows()
    {
        IceSwitchboard board = new();
        (IceSwitchboard.Peer a, IceSwitchboard.Peer b) = Connect(
            board,
            [IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.1.0.1")],
            [IPAddress.Parse("10.0.0.2"), IPAddress.Parse("10.1.0.2")]
        );
        _ = board.Stream(TimeSpan.FromSeconds(1));
        IPAddress dead = a.SelectedAddress!;

        // The path dies with no interface event, as in a cellular dead zone: only the silence shows it.
        board.Cut(dead);
        TimeSpan cutAt = board.Now;
        while (
            (a.SelectedAddress!.Equals(dead) || IsOnPeerOf(b, dead))
            && board.Now - cutAt < Timings.ConsentTimeout
        )
        {
            _ = board.Stream(TimeSpan.FromMilliseconds(100));
        }

        // Silence for the receiving timeout, the damping delay, then a round of fast pings on the
        // alternates: seconds, where consent alone takes thirty.
        TimeSpan failover = board.Now - cutAt;
        Assert.IsLessThan(
            Timings.ReceivingTimeout + Timings.SwitchingDelay + TimeSpan.FromSeconds(2),
            failover,
            $"both sides move off the dead path in seconds; took {failover}."
        );
        (int sent, int fromA, int fromB) = board.Stream(TimeSpan.FromSeconds(1));
        Assert.AreEqual(sent, fromA, "media flows over the new path.");
        Assert.AreEqual(sent, fromB);
        Assert.IsTrue(a.Connected && b.Connected);
    }

    [TestMethod]
    public void PreferredPathReturning_TakesTheMediaBack()
    {
        IceSwitchboard board = new();
        var v6 = IPAddress.Parse("2001:db8::1");
        (IceSwitchboard.Peer a, IceSwitchboard.Peer b) = Connect(
            board,
            [v6, IPAddress.Parse("192.0.2.1")],
            [IPAddress.Parse("2001:db8::2"), IPAddress.Parse("192.0.2.2")]
        );
        Assert.AreEqual(v6, a.SelectedAddress, "IPv6 ranks first.");

        board.Cut(v6);
        _ = board.Stream(TimeSpan.FromSeconds(8));
        Assert.AreEqual(AddressFamily.InterNetwork, a.SelectedAddress!.AddressFamily);

        board.Uncut(v6);
        _ = board.Stream(Timings.StandbyPingInterval + TimeSpan.FromSeconds(5));
        Assert.AreEqual(v6, a.SelectedAddress, "media returns to the higher-priority path.");
        Assert.AreEqual(AddressFamily.InterNetworkV6, b.SelectedAddress!.AddressFamily);
        (int sent, int fromA, int fromB) = board.Stream(TimeSpan.FromSeconds(1));
        Assert.AreEqual(sent, fromA);
        Assert.AreEqual(sent, fromB);
    }

    [TestMethod]
    public void OneWayMedia_StaysOnTheSelectedPair()
    {
        IceSwitchboard board = new();
        (IceSwitchboard.Peer a, IceSwitchboard.Peer b) = Connect(
            board,
            [IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.1.0.1")],
            [IPAddress.Parse("10.0.0.2"), IPAddress.Parse("10.1.0.2")]
        );
        var selectedA = a.Machine.Selected;
        var selectedB = b.Machine.Selected;

        // A publisher with no media coming back: the selected pair sees only its keep-alives.
        var step = TimeSpan.FromMilliseconds(20);
        for (TimeSpan elapsed = TimeSpan.Zero; elapsed < TimeSpan.FromMinutes(2); elapsed += step)
        {
            Assert.IsTrue(board.SendData(a));
            board.Run(step);
        }

        Assert.AreEqual(selectedA, a.Machine.Selected);
        Assert.AreEqual(selectedB, b.Machine.Selected);
    }

    [TestMethod]
    [DataRow(true, DisplayName = "trickled")]
    [DataRow(false, DisplayName = "peer-reflexive, no signaling")]
    public void InterfaceComingUp_IsCheckedMidSession_AndTakesOverWhenTheOldOneGoes(bool trickle)
    {
        IceSwitchboard board = new();
        var wifi = IPAddress.Parse("10.0.0.1");
        var cellular = IPAddress.Parse("10.1.0.1");
        (IceSwitchboard.Peer a, IceSwitchboard.Peer b) = Connect(
            board,
            [wifi],
            [IPAddress.Parse("10.0.0.2"), IPAddress.Parse("10.1.0.2")]
        );
        board.Trickle = trickle;

        // The modem attaches: no restart, its pairs are checked alongside the live one.
        board.AddInterface(a, cellular);
        _ = board.Stream(TimeSpan.FromSeconds(2));
        Assert.AreEqual(
            wifi,
            a.SelectedAddress,
            "media stays where it is while the new pairs check."
        );

        // Wi-Fi drops with an interface event: the warm cellular pair takes over at once.
        board.RemoveInterface(a, wifi);
        TimeSpan removedAt = board.Now;
        Assert.AreEqual(cellular, a.SelectedAddress);
        while (IsOnPeerOf(b, wifi) && board.Now - removedAt < TimeSpan.FromSeconds(5))
        {
            _ = board.Stream(TimeSpan.FromMilliseconds(20));
        }

        Assert.IsLessThan(
            TimeSpan.FromSeconds(1),
            board.Now - removedAt,
            "the controlled side follows within the time the media takes to arrive."
        );
        (int sent, int fromA, int fromB) = board.Stream(TimeSpan.FromSeconds(1));
        Assert.AreEqual(sent, fromA);
        Assert.AreEqual(sent, fromB);
        Assert.IsTrue(a.Connected && b.Connected);
    }

    [TestMethod]
    public void SelectedInterfaceGoing_WithNoAlternate_ChecksAgain()
    {
        IceSwitchboard board = new();
        var wifi = IPAddress.Parse("10.0.0.1");
        (IceSwitchboard.Peer a, _) = Connect(board, [wifi], [IPAddress.Parse("10.0.0.2")]);

        board.RemoveInterface(a, wifi);

        Assert.IsNull(a.Machine.Selected);
        Assert.AreEqual(IceConnectionState.Checking, a.Machine.State);

        board.AddInterface(a, IPAddress.Parse("10.1.0.1"));
        Assert.IsTrue(board.RunUntil(() => a.Connected, TimeSpan.FromSeconds(10)));
    }

    [TestMethod]
    public void Connected_ChecksAtTheKeepAliveCadence_NotThePacingRate()
    {
        IceSwitchboard board = new();
        _ = Connect(board, [IPAddress.Parse("10.0.0.1")], [IPAddress.Parse("10.0.0.2")]);
        int before = board.ChecksDelivered;

        _ = board.Stream(TimeSpan.FromMinutes(1));

        // Each side pings the selected pair every 2.5 s once its round trip is stable: about 48 checks a
        // minute. A check answering every request would bounce at the 50 ms pacing rate, about 2400.
        int checks = board.ChecksDelivered - before;
        Assert.IsLessThan(80, checks, $"{checks} checks in a quiet minute.");
    }

    [TestMethod]
    public void RepairsOnTheStandbyPath_LeaveBothSelectionsWhereTheyAre()
    {
        IceSwitchboard board = new();
        (IceSwitchboard.Peer a, IceSwitchboard.Peer b) = Connect(
            board,
            [IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.1.0.1")],
            [IPAddress.Parse("10.0.0.2"), IPAddress.Parse("10.1.0.2")]
        );
        _ = board.Stream(TimeSpan.FromSeconds(3));
        var selectedA = a.Machine.Selected;
        var selectedB = b.Machine.Selected;
        Assert.IsNotNull(a.Machine.Standby(board.Now), "the other interface stands by.");
        Assert.AreNotEqual(selectedA!.Value.Local, a.Machine.Standby(board.Now)!.Value.Local);

        // Media on the selected pair, a repair on the standby for every few media packets.
        var step = TimeSpan.FromMilliseconds(20);
        int repairs = 0;
        for (TimeSpan elapsed = TimeSpan.Zero; elapsed < TimeSpan.FromSeconds(20); elapsed += step)
        {
            Assert.IsTrue(board.SendData(a));
            Assert.IsTrue(board.SendData(b));
            repairs += board.SendOnStandby(a) ? 1 : 0;
            board.Run(step);
        }

        Assert.IsGreaterThan(500, repairs);
        Assert.AreEqual(selectedA, a.Machine.Selected);
        Assert.AreEqual(selectedB, b.Machine.Selected, "the controlled side follows nominations.");
    }

    private static bool IsOnPeerOf(IceSwitchboard.Peer peer, IPAddress address) =>
        peer.Machine.Selected?.Remote.Address.Equals(address) == true;

    // Connects a pair and lets hot-standby keep-alives validate the alternates.
    private static (IceSwitchboard.Peer A, IceSwitchboard.Peer B) Connect(
        IceSwitchboard board,
        IPAddress[] controlling,
        IPAddress[] controlled
    )
    {
        (IceSwitchboard.Peer a, IceSwitchboard.Peer b) = board.Pair(
            controlling,
            controlled,
            Timings
        );
        Assert.IsTrue(
            board.RunUntil(() => a.Connected && b.Connected, TimeSpan.FromSeconds(10)),
            "the machines connect within ten seconds"
        );
        board.Run(Timings.StandbyPingInterval * 2);
        return (a, b);
    }
}
