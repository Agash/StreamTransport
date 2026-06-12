using Agash.StreamTransport.Signaling;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

[TestClass]
public sealed class SignalingRouterTests
{
    private static readonly RoomCode Room = new("room42");

    [TestMethod]
    public async Task Publisher_JoiningUnknownRoom_CreatesItAndIsWelcomed()
    {
        var router = new SignalingRouter();
        var transport = new CapturingTransport();
        await using ISignalingSession session = router.Connect(transport);

        await session.ReceiveAsync(new HelloMessage(SignalingProtocol.Version, PeerRole.Publisher, Room));

        WelcomeMessage welcome = transport.Single<WelcomeMessage>();
        Assert.AreEqual(Room, welcome.RoomState.Code);
        Assert.AreEqual(welcome.PeerId, session.PeerId);
        Assert.AreEqual(0, welcome.RoomState.Peers.Count, "the publisher is the only peer and is not listed to itself.");
    }

    [TestMethod]
    public async Task Subscriber_JoiningUnknownRoom_IsRejected()
    {
        var router = new SignalingRouter();
        var transport = new CapturingTransport();
        await using ISignalingSession session = router.Connect(transport);

        await session.ReceiveAsync(new HelloMessage(SignalingProtocol.Version, PeerRole.Subscriber, Room));

        SignalingErrorMessage error = transport.Single<SignalingErrorMessage>();
        Assert.AreEqual(SignalingErrorCode.RoomNotFound, error.Code);
        Assert.IsNull(session.PeerId);
    }

    [TestMethod]
    public async Task VersionMismatch_IsRejected()
    {
        var router = new SignalingRouter();
        var transport = new CapturingTransport();
        await using ISignalingSession session = router.Connect(transport);

        await session.ReceiveAsync(new HelloMessage(SignalingProtocol.Version + 1, PeerRole.Publisher, Room));

        Assert.AreEqual(SignalingErrorCode.VersionMismatch, transport.Single<SignalingErrorMessage>().Code);
    }

    [TestMethod]
    public async Task Subscriber_JoiningPopulatedRoom_SeesPublisherAndPublisherIsNotified()
    {
        var router = new SignalingRouter();
        var pubTransport = new CapturingTransport();
        var subTransport = new CapturingTransport();
        await using ISignalingSession publisher = router.Connect(pubTransport);
        await using ISignalingSession subscriber = router.Connect(subTransport);

        await publisher.ReceiveAsync(new HelloMessage(SignalingProtocol.Version, PeerRole.Publisher, Room));
        await subscriber.ReceiveAsync(new HelloMessage(SignalingProtocol.Version, PeerRole.Subscriber, Room));

        // The subscriber's welcome lists the publisher.
        WelcomeMessage subWelcome = subTransport.Single<WelcomeMessage>();
        Assert.AreEqual(1, subWelcome.RoomState.Peers.Count);
        Assert.AreEqual(PeerRole.Publisher, subWelcome.RoomState.Peers[0].Role);
        Assert.AreEqual(publisher.PeerId, subWelcome.RoomState.Peers[0].PeerId);

        // The publisher is told the subscriber joined.
        PeerJoinedMessage joined = pubTransport.Single<PeerJoinedMessage>();
        Assert.AreEqual(subscriber.PeerId, joined.Peer.PeerId);
        Assert.AreEqual(PeerRole.Subscriber, joined.Peer.Role);
    }

    [TestMethod]
    public async Task Sdp_IsRoutedToTarget_WithFromStamped()
    {
        var router = new SignalingRouter();
        var pubTransport = new CapturingTransport();
        var subTransport = new CapturingTransport();
        await using ISignalingSession publisher = router.Connect(pubTransport);
        await using ISignalingSession subscriber = router.Connect(subTransport);

        await publisher.ReceiveAsync(new HelloMessage(SignalingProtocol.Version, PeerRole.Publisher, Room));
        await subscriber.ReceiveAsync(new HelloMessage(SignalingProtocol.Version, PeerRole.Subscriber, Room));
        PeerId subId = subscriber.PeerId!.Value;
        PeerId pubId = publisher.PeerId!.Value;

        await publisher.ReceiveAsync(new SdpMessage(SdpKind.Offer, "sdp-body", To: subId));

        SdpMessage routed = subTransport.Single<SdpMessage>();
        Assert.AreEqual("sdp-body", routed.Sdp);
        Assert.AreEqual(pubId, routed.From, "the router stamps From from the originating session, not the client.");
        Assert.AreEqual(subId, routed.To);
    }

    [TestMethod]
    public async Task PeerControl_IsRoutedToTarget_WithFromStamped()
    {
        var router = new SignalingRouter();
        var pubTransport = new CapturingTransport();
        var subTransport = new CapturingTransport();
        await using ISignalingSession publisher = router.Connect(pubTransport);
        await using ISignalingSession subscriber = router.Connect(subTransport);

        await publisher.ReceiveAsync(new HelloMessage(SignalingProtocol.Version, PeerRole.Publisher, Room));
        await subscriber.ReceiveAsync(new HelloMessage(SignalingProtocol.Version, PeerRole.Subscriber, Room));
        PeerId subId = subscriber.PeerId!.Value;
        PeerId pubId = publisher.PeerId!.Value;

        await publisher.ReceiveAsync(new PeerControlMessage("stream.alpha", "1", To: subId));

        PeerControlMessage routed = subTransport.Single<PeerControlMessage>();
        Assert.AreEqual("stream.alpha", routed.Topic);
        Assert.AreEqual("1", routed.Payload);
        Assert.AreEqual(pubId, routed.From, "the router stamps From from the originating session, not the client.");
        Assert.AreEqual(subId, routed.To);
    }

    [TestMethod]
    public async Task PeerControl_WithoutTarget_FansOutToTheRestOfTheRoom()
    {
        var router = new SignalingRouter();
        var pubTransport = new CapturingTransport();
        var subTransport = new CapturingTransport();
        await using ISignalingSession publisher = router.Connect(pubTransport);
        await using ISignalingSession subscriber = router.Connect(subTransport);

        await publisher.ReceiveAsync(new HelloMessage(SignalingProtocol.Version, PeerRole.Publisher, Room));
        await subscriber.ReceiveAsync(new HelloMessage(SignalingProtocol.Version, PeerRole.Subscriber, Room));
        PeerId pubId = publisher.PeerId!.Value;

        await publisher.ReceiveAsync(new PeerControlMessage("room.note", "hi", To: null));

        PeerControlMessage fanned = subTransport.Single<PeerControlMessage>();
        Assert.AreEqual("room.note", fanned.Topic);
        Assert.AreEqual(pubId, fanned.From);
        Assert.IsFalse(pubTransport.Any<PeerControlMessage>(), "the sender does not receive its own broadcast.");
    }

    [TestMethod]
    public async Task PeerLeaving_NotifiesOthers()
    {
        var router = new SignalingRouter();
        var pubTransport = new CapturingTransport();
        var subTransport = new CapturingTransport();
        ISignalingSession publisher = router.Connect(pubTransport);
        await using ISignalingSession subscriber = router.Connect(subTransport);

        await publisher.ReceiveAsync(new HelloMessage(SignalingProtocol.Version, PeerRole.Publisher, Room));
        await subscriber.ReceiveAsync(new HelloMessage(SignalingProtocol.Version, PeerRole.Subscriber, Room));
        PeerId pubId = publisher.PeerId!.Value;

        await publisher.DisposeAsync();

        PeerLeftMessage left = subTransport.Single<PeerLeftMessage>();
        Assert.AreEqual(pubId, left.PeerId);
    }

    /// <summary>An <see cref="ISignalingPeerTransport"/> that records what the router sends to a peer.</summary>
    private sealed class CapturingTransport : ISignalingPeerTransport
    {
        private readonly List<SignalingMessage> _sent = [];

        public ValueTask SendAsync(SignalingMessage message, CancellationToken cancellationToken = default)
        {
            lock (_sent)
            {
                _sent.Add(message);
            }

            return ValueTask.CompletedTask;
        }

        public T Single<T>() where T : SignalingMessage
        {
            lock (_sent)
            {
                return _sent.OfType<T>().Single();
            }
        }

        public bool Any<T>() where T : SignalingMessage
        {
            lock (_sent)
            {
                return _sent.OfType<T>().Any();
            }
        }
    }
}
