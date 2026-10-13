using System.Net;
using Agash.StreamTransport.WebRtc.Ice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.WebRtc.Tests;

// RFC 8445 section 8.1.2: an agent whose checks cannot succeed reports Failed, so a session to an
// unreachable peer ends instead of waiting for ever. Time jumps to each timeout.
[TestClass]
public sealed class IceFailureTests
{
    [TestMethod]
    public void UnreachablePeer_FailsOnceEveryCheckHasFailedAndTheCandidatesStop()
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

        Assert.IsLessThan(
            IceTimings.Default.FailureTimeout,
            failedAt,
            "the failed checks end it before the timeout"
        );
    }

    [TestMethod]
    public void PeerThatNeverSendsACandidate_FailsAtTheFailureTimeout()
    {
        IceStateMachine machine = Machine(out List<IceConnectionState> states);

        TimeSpan failedAt = RunUntilFailed(machine, states, TimeSpan.FromSeconds(60));

        Assert.AreEqual(IceTimings.Default.FailureTimeout.TotalSeconds, failedAt.TotalSeconds, 0.2);
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
