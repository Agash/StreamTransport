using Agash.StreamTransport.Signaling;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// Exercises the generic peer-control channel at the <see cref="RoomClient"/> level over an in-memory
/// duplex transport driven directly by a <see cref="SignalingRouter"/> - no sockets. This proves the channel
/// end to end on the client side AND the "bring your own transport" path a host uses to
/// run the room protocol over an existing SignalR hub instead of a WebSocket.
/// </summary>
[TestClass]
public sealed class RoomClientControlTests
{
    private static readonly RoomCode Room = new("ctrlroom");

    [TestMethod]
    public async Task ControlMessage_RoundTripsThroughRouter_WithFromStamped()
    {
        var router = new SignalingRouter();
        await using var pubLink = new InMemoryClientLink(router);
        await using var subLink = new InMemoryClientLink(router);

        await using RoomClient publisher = await RoomClient.ConnectAsync(pubLink, Room, PeerRole.Publisher);
        await using RoomClient subscriber = await RoomClient.ConnectAsync(subLink, Room, PeerRole.Subscriber);

        var received = new TaskCompletionSource<PeerControlMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.ControlReceived += m => received.TrySetResult(m);

        await publisher.SendControlAsync("stream.alpha", "1", subscriber.Self);

        PeerControlMessage got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("stream.alpha", got.Topic);
        Assert.AreEqual("1", got.Payload);
        Assert.AreEqual(publisher.Self, got.From, "the router stamps From from the sending session.");
    }

    [TestMethod]
    public async Task ControlMessage_WithoutTarget_FansOutToOtherPeers()
    {
        var router = new SignalingRouter();
        await using var pubLink = new InMemoryClientLink(router);
        await using var subLink = new InMemoryClientLink(router);

        await using RoomClient publisher = await RoomClient.ConnectAsync(pubLink, Room, PeerRole.Publisher);
        await using RoomClient subscriber = await RoomClient.ConnectAsync(subLink, Room, PeerRole.Subscriber);

        var received = new TaskCompletionSource<PeerControlMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.ControlReceived += m => received.TrySetResult(m);

        await publisher.SendControlAsync("room.note", "hi", to: null); // unaddressed -> fan out to the room.

        PeerControlMessage got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("room.note", got.Topic);
        Assert.AreEqual(publisher.Self, got.From);
    }

    /// <summary>
    /// A sockets-free <see cref="IDuplexSignalingTransport"/> that drives a router session directly: the
    /// client's outbound messages go straight to <see cref="ISignalingSession.ReceiveAsync"/>, and the
    /// router's outbound messages (via the per-peer <see cref="ISignalingPeerTransport"/>) are raised back
    /// as <see cref="MessageReceived"/>.
    /// </summary>
    private sealed class InMemoryClientLink : IDuplexSignalingTransport
    {
        private readonly ISignalingSession _session;

        public InMemoryClientLink(SignalingRouter router) => _session = router.Connect(new RouterSink(this));

        public event Func<SignalingMessage, Task>? MessageReceived;

        // Client -> router.
        public ValueTask SendAsync(SignalingMessage message, CancellationToken cancellationToken = default) =>
            _session.ReceiveAsync(message, cancellationToken);

        // Delivery is push-driven by the router sink, so the pump just idles until cancelled.
        public Task RunAsync(CancellationToken cancellationToken = default) =>
            Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(static _ => { }, TaskScheduler.Default);

        public ValueTask DisposeAsync() => _session.DisposeAsync();

        private Task Deliver(SignalingMessage message) => MessageReceived?.Invoke(message) ?? Task.CompletedTask;

        // Router -> client.
        private sealed class RouterSink(InMemoryClientLink link) : ISignalingPeerTransport
        {
            public ValueTask SendAsync(SignalingMessage message, CancellationToken cancellationToken = default)
            {
                _ = link.Deliver(message);
                return ValueTask.CompletedTask;
            }
        }
    }
}
