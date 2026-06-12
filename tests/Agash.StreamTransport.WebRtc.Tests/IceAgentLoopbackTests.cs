using System.Net;
using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.Ice;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// Drives two real <see cref="IceAgent"/> instances over loopback UDP, wiring each agent's gathered
/// candidates into the other (simulating trickle signaling), and asserts the full ICE handshake completes
/// and a media datagram flows across the selected pair.
/// </summary>
[TestClass]
public sealed class IceAgentLoopbackTests
{
    [TestMethod]
    [Timeout(20_000)]
    public async Task TwoAgents_OverLoopback_ConnectAndCarryData()
    {
        var offererCreds = IceCredentials.Generate();
        var answererCreds = IceCredentials.Generate();

        await using var offerer = new IceAgent(offererCreds, IceRole.Controlling, includeLoopback: true);
        await using var answerer = new IceAgent(answererCreds, IceRole.Controlled, includeLoopback: true);

        offerer.SetRemoteCredentials(answererCreds);
        answerer.SetRemoteCredentials(offererCreds);

        // Trickle: each agent's local candidates become the other's remote candidates.
        offerer.LocalCandidateGathered += c => answerer.AddRemoteCandidate(c);
        answerer.LocalCandidateGathered += c => offerer.AddRemoteCandidate(c);

        var offererConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answererConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        offerer.StateChanged += s => { if (s == IceConnectionState.Connected) { offererConnected.TrySetResult(); } };
        answerer.StateChanged += s => { if (s == IceConnectionState.Connected) { answererConnected.TrySetResult(); } };

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        answerer.DataReceived += (data, _) => received.TrySetResult(data.ToArray());

        offerer.Start();
        answerer.Start();

        await Task.WhenAll(offererConnected.Task, answererConnected.Task).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.AreEqual(IceConnectionState.Connected, offerer.State);
        Assert.AreEqual(IceConnectionState.Connected, answerer.State);

        // Send a non-STUN datagram (stand-in for DTLS/SRTP) over the selected pair; it must arrive.
        byte[] payload = [0x16, 0xfe, 0xfd, 0x01, 0x02, 0x03]; // looks like a DTLS record (first byte 0x16).
        await offerer.SendAsync(payload);

        byte[] got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(payload, got);
    }
}
