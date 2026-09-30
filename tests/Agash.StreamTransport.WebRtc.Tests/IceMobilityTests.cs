using System.Net;
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
        board.Run(Timings.ConsentInterval * 2);
        return (a, b);
    }
}
