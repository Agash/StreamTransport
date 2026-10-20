using System.Net;
using Agash.StreamTransport.WebRtc.Ice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.WebRtc.Tests;

// RFC 8445 section 8.1.2: an agent whose checks cannot succeed reports Failed, so a session to an
// unreachable peer ends instead of waiting for ever. Time jumps to each timeout.
[TestClass]
public sealed class IceFailureTests
{
    // RFC 8863 section 4: every pair failing does not end it before the PAC timer, since the peer's own
    // checks may still reveal a peer-reflexive candidate.
    [TestMethod]
    public void UnreachablePeer_FailsAtThePacTimerNotBefore()
    {
        IceStateMachine machine = Machine(out List<IceConnectionState> states);
        Assert.IsTrue(
            IceCandidate.TryParse(
                "candidate:1 1 udp 2130706431 10.0.0.2 6000 typ host",
                out IceCandidate remote
            )
        );
        machine.AddRemoteCandidate(remote);

        TimeSpan failedAt = RunUntilFailed(machine, states, TimeSpan.FromSeconds(60));

        Assert.AreEqual(IceTimings.Default.FailureTimeout.TotalSeconds, failedAt.TotalSeconds, 0.2);
        Assert.AreEqual(39.5, IceTimings.Default.FailureTimeout.TotalSeconds);
    }

    [TestMethod]
    public void PeerThatNeverSendsACandidate_FailsAtTheFailureTimeout()
    {
        IceStateMachine machine = Machine(out List<IceConnectionState> states);

        TimeSpan failedAt = RunUntilFailed(machine, states, TimeSpan.FromSeconds(60));

        Assert.AreEqual(IceTimings.Default.FailureTimeout.TotalSeconds, failedAt.TotalSeconds, 0.2);
    }

    // RFC 8838 section 14: after the peer's end-of-candidates, a candidate it sends is ignored and never
    // checked; an ICE restart begins a session that takes candidates again.
    [TestMethod]
    public void EndOfCandidates_LaterCandidatesAreIgnoredUntilARestart()
    {
        IceStateMachine machine = new(
            IceCredentials.Generate(),
            IceRole.Controlling,
            IceTimings.Default,
            NullLogger.Instance
        );
        machine.SetRemoteCredentials(IceCredentials.Generate());
        machine.AddLocalEndpoint(0, new IPEndPoint(IPAddress.Parse("10.0.0.1"), 5000));
        Assert.IsTrue(
            IceCandidate.TryParse(
                "candidate:1 1 udp 2130706431 10.0.0.2 6000 typ host",
                out IceCandidate late
            )
        );
        machine.Start(TimeSpan.Zero);

        machine.EndRemoteCandidates();
        machine.AddRemoteCandidate(late);

        Assert.DoesNotContain(late.Endpoint, Checked(machine, TimeSpan.Zero));

        machine.Restart(IceCredentials.Generate());
        machine.SetRemoteCredentials(IceCredentials.Generate());
        machine.AddLocalEndpoint(1, new IPEndPoint(IPAddress.Parse("10.0.0.1"), 5001));
        machine.AddRemoteCandidate(late);

        Assert.Contains(late.Endpoint, Checked(machine, TimeSpan.FromSeconds(5)));
    }

    // Where checks went over five seconds from the start.
    private static List<IPEndPoint> Checked(IceStateMachine machine, TimeSpan start)
    {
        List<IPEndPoint> destinations = [];
        for (
            TimeSpan now = start;
            now < start + TimeSpan.FromSeconds(5);
            now += TimeSpan.FromMilliseconds(50)
        )
        {
            machine.HandleTimeout(now);
            while (machine.TryPollTransmit(out IceTransmit transmit))
            {
                destinations.Add(transmit.Destination);
            }

            while (machine.TryPollEvent(out _)) { }
        }

        return destinations;
    }

    // RFC 8489 section 6.2.1: a server-reflexive request with no answer goes again, seven times in all,
    // and is given up after.
    [TestMethod]
    public void UnansweredGatherRequest_IsRetransmittedThenGivenUp()
    {
        IceStateMachine machine = new(
            IceCredentials.Generate(),
            IceRole.Controlling,
            IceTimings.Default,
            NullLogger.Instance
        );
        var server = new IPEndPoint(IPAddress.Parse("10.9.9.9"), 3478);
        machine.AddLocalEndpoint(0, new IPEndPoint(IPAddress.Parse("10.0.0.1"), 5000));
        machine.AddStunServer(server);
        machine.Start(TimeSpan.Zero);

        int sent = 0;
        while (machine.NextTimeout is { } next && next <= TimeSpan.FromSeconds(60))
        {
            machine.HandleTimeout(next);
            while (machine.TryPollTransmit(out IceTransmit transmit))
            {
                sent += transmit.Destination.Equals(server) ? 1 : 0;
            }

            while (machine.TryPollEvent(out _)) { }
        }

        Assert.AreEqual(7, sent);
        Assert.AreEqual(0, machine.PendingGathers);
    }

    // RFC 9725 section 4.3.3, RFC 8839 section 4.4.1.1.1: after a restart only the new session's candidates
    // are checked.
    [TestMethod]
    public void Restart_ForgetsTheOldSessionsCandidates()
    {
        IceStateMachine machine = new(
            IceCredentials.Generate(),
            IceRole.Controlling,
            IceTimings.Default,
            NullLogger.Instance
        );
        machine.SetRemoteCredentials(IceCredentials.Generate());
        machine.AddLocalEndpoint(0, new IPEndPoint(IPAddress.Parse("10.0.0.1"), 5000));
        Assert.IsTrue(
            IceCandidate.TryParse(
                "candidate:1 1 udp 2130706431 10.0.0.2 6000 typ host",
                out IceCandidate old
            )
        );
        Assert.IsTrue(
            IceCandidate.TryParse(
                "candidate:2 1 udp 2130706431 10.0.0.3 6000 typ host",
                out IceCandidate fresh
            )
        );
        machine.AddRemoteCandidate(old);
        machine.Start(TimeSpan.Zero);

        machine.Restart(IceCredentials.Generate());
        machine.SetRemoteCredentials(IceCredentials.Generate());
        machine.AddLocalEndpoint(1, new IPEndPoint(IPAddress.Parse("10.0.0.1"), 5001));
        machine.AddRemoteCandidate(fresh);
        HashSet<IPEndPoint> checkedAddresses = [];
        for (
            TimeSpan now = TimeSpan.Zero;
            now < TimeSpan.FromSeconds(5);
            now += TimeSpan.FromMilliseconds(50)
        )
        {
            machine.HandleTimeout(now);
            while (machine.TryPollTransmit(out IceTransmit transmit))
            {
                _ = checkedAddresses.Add(transmit.Destination);
            }
        }

        CollectionAssert.Contains(checkedAddresses.ToList(), fresh.Endpoint);
        CollectionAssert.DoesNotContain(checkedAddresses.ToList(), old.Endpoint);
    }

    private static IceStateMachine Machine(out List<IceConnectionState> states)
    {
        IceStateMachine machine = new(
            IceCredentials.Generate(),
            IceRole.Controlling,
            IceTimings.Default,
            NullLogger.Instance
        );
        machine.AddLocalEndpoint(0, new IPEndPoint(IPAddress.Parse("10.0.0.1"), 5000));
        machine.SetRemoteCredentials(IceCredentials.Generate());
        machine.Start(TimeSpan.Zero);
        states = [];
        return machine;
    }

    private static TimeSpan RunUntilFailed(
        IceStateMachine machine,
        List<IceConnectionState> states,
        TimeSpan limit
    )
    {
        while (machine.NextTimeout is { } next && next <= limit)
        {
            machine.HandleTimeout(next);
            while (machine.TryPollTransmit(out _)) { }
            while (machine.TryPollEvent(out IceEvent iceEvent))
            {
                if (iceEvent.Candidate is null)
                {
                    states.Add(iceEvent.State);
                }
            }

            if (machine.State == IceConnectionState.Failed)
            {
                Assert.AreEqual(IceConnectionState.Failed, states[^1]);
                return next;
            }
        }

        Assert.Fail($"never failed; states {string.Join(", ", states)}");
        return default;
    }
}
