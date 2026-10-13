using System.Net;
using Agash.StreamTransport.WebRtc.Ice;
using Agash.StreamTransport.WebRtc.Stun;

namespace Agash.StreamTransport.WebRtc.Tests;

// RFC 8445 section 7.3.1.1: agents that start in the same role settle it by tie-breaker and connect.
[TestClass]
public sealed class IceRoleConflictTests
{
    [TestMethod]
    [DataRow(IceRole.Controlling)]
    [DataRow(IceRole.Controlled)]
    public void AgentsInTheSameRole_SettleItAndConnect(IceRole both)
    {
        IceSwitchboard board = new();
        (IceSwitchboard.Peer a, IceSwitchboard.Peer b) = board.Pair(
            [IPAddress.Parse("10.0.0.1")],
            [IPAddress.Parse("10.0.0.2")],
            firstRole: both,
            secondRole: both
        );

        Assert.IsTrue(
            board.RunUntil(
                () => a.Machine.Selected is not null && b.Machine.Selected is not null,
                TimeSpan.FromSeconds(10)
            ),
            "the agents never connected"
        );
        Assert.AreNotEqual(a.Machine.Role, b.Machine.Role, "one of them changed role");
    }

    // RFC 8489 section 14.5: integrity does not cover what follows it, so it does not count.
    [TestMethod]
    public void Reader_IgnoresAttributesAfterMessageIntegrity()
    {
        byte[] key = [1, 2, 3, 4];
        byte[] buffer = new byte[256];
        StunMessageWriter writer = new(
            buffer,
            StunMessageClass.Request,
            StunMethod.Binding,
            new byte[StunHeader.TransactionIdLength]
        );
        writer.AddAttribute(StunAttributeType.Priority, [0, 0, 0, 1]);
        writer.AddMessageIntegrity(key);
        writer.AddAttribute(StunAttributeType.UseCandidate, default);
        writer.AddFingerprint();

        Assert.IsTrue(
            StunMessageReader.TryParse(buffer.AsSpan(0, writer.Length), out StunMessageReader read)
        );
        Assert.IsTrue(read.TryFindAttribute(StunAttributeType.Priority, out _));
        Assert.IsFalse(read.TryFindAttribute(StunAttributeType.UseCandidate, out _));
        Assert.IsTrue(read.TryFindAttribute(StunAttributeType.Fingerprint, out _));
    }
}
