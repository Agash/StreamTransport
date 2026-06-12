using System.Security.Cryptography;
using System.Text;
using Agash.StreamTransport.Stun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

[TestClass]
public sealed class SignalingProtocolTests
{
    [TestMethod]
    public void SignalingJson_RoundTrips_PreservingTypeAndFields()
    {
        var welcome = new WelcomeMessage(
            new PeerId(7),
            new RoomState(new RoomCode("abcdef"), [new PeerInfo(new PeerId(3), PeerRole.Publisher)],
                [new IceServer(["stun:host:3478"]), new IceServer(["turn:host:3478"], "user", "cred")]));

        string json = SignalingJson.Serialize(welcome);
        var back = SignalingJson.Deserialize(json) as WelcomeMessage;

        Assert.IsNotNull(back);
        Assert.AreEqual(new PeerId(7), back!.PeerId);
        Assert.AreEqual(new RoomCode("abcdef"), back.RoomState.Code);
        Assert.AreEqual(PeerRole.Publisher, back.RoomState.Peers[0].Role);
        Assert.AreEqual("turn:host:3478", back.RoomState.IceServers[1].Urls[0]);
        Assert.AreEqual("cred", back.RoomState.IceServers[1].Credential);
    }

    [TestMethod]
    public void SignalingJson_PeerIdIsNumber_RoomCodeIsString_DiscriminatorIsType()
    {
        string json = SignalingJson.Serialize(new HelloMessage(1, PeerRole.Subscriber, new RoomCode("xyz")));

        StringAssert.Contains(json, "\"type\":\"hello\"");
        StringAssert.Contains(json, "\"room\":\"xyz\"");
        StringAssert.Contains(json, "\"role\":\"subscriber\"");
    }

    [TestMethod]
    public void SignalingJson_RoundTrips_PeerControlMessage()
    {
        string json = SignalingJson.Serialize(new PeerControlMessage("stream.alpha", "1", To: new PeerId(3)));
        StringAssert.Contains(json, "\"type\":\"peer_control\"");

        var back = SignalingJson.Deserialize(json) as PeerControlMessage;
        Assert.IsNotNull(back);
        Assert.AreEqual("stream.alpha", back!.Topic);
        Assert.AreEqual("1", back.Payload);
        Assert.AreEqual(new PeerId(3), back.To);
        Assert.IsNull(back.From);
    }

    [TestMethod]
    public void Coturn_MintsTurnRestEphemeralCredentials()
    {
        const string secret = "topsecret";
        var clock = new FixedClock(DateTimeOffset.FromUnixTimeSeconds(1_000_000));
        var provider = new CoturnSharedSecretIceServerProvider(
            stunUrls: ["stun:turn.example.com:3478"],
            turnUrls: ["turn:turn.example.com:3478?transport=udp"],
            sharedSecret: secret,
            credentialLifetime: TimeSpan.FromSeconds(600),
            timeProvider: clock);

        IReadOnlyList<IceServer> servers = provider.GetIceServersForPeer();

        IceServer turn = servers.Single(s => s.Urls[0].StartsWith("turn:", StringComparison.Ordinal));
        Assert.AreEqual("1000600", turn.Username, "username is the unix expiry (now + lifetime).");

        string expected = Convert.ToBase64String(
            HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("1000600")));
        Assert.AreEqual(expected, turn.Credential);
        Assert.IsTrue(servers.Any(s => s.Urls[0].StartsWith("stun:", StringComparison.Ordinal)), "STUN is advertised too.");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
