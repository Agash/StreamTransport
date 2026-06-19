using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Agash.StreamTransport.Signaling;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// End-to-end through a real WebSocket relay running in-process: a publisher and a subscriber each
/// connect a <see cref="RoomClient"/> to the relay, the publisher fans audio out via
/// <see cref="MediaPublisher"/>, and the subscriber decodes it via <see cref="MediaSubscriber"/>. This
/// exercises the whole signaling + media stack (room router, peer routing, ICE, SRTP, Opus) over loopback
/// WebRTC, not just an in-memory channel. The relay is in-process (no external dependency), so this runs in
/// CI. The DTLS handshake runs on a dedicated (non-pool) thread, so these no longer need to be serialized.
/// </summary>
// Categorized Integration: these drive the full real-socket stack (HttpListener relay + loopback ICE + DTLS +
// SRTP + RTP), which is intermittently slow-or-stuck to establish under a loaded test host (a "fast or never"
// connect race specific to rapid in-process loopback setup; cross-machine runs connect reliably). The default
// CI gate excludes TestCategory=Integration and a separate non-gating job runs them, so a transient connect
// flake never blocks the gate. The per-component logic is covered deterministically by the WebRtc unit tests
// and the in-memory loopback tests. Tracked: Agash/StreamTransport#1.
[TestClass]
[TestCategory("Integration")]
public sealed class RelayIntegrationTests
{
    [TestMethod]
    [Timeout(60_000)]
    public async Task PublisherAndSubscriber_OverRealRelay_DeliverDecodedAudio()
    {
        await using var relay = InProcessRelay.Start();
        var room = new RoomCode("itroom");

        await using RoomClient publisherRoom = await RoomClient.ConnectAsync(relay.WebSocketUri, room, PeerRole.Publisher);
        await using RoomClient subscriberRoom = await RoomClient.ConnectAsync(relay.WebSocketUri, room, PeerRole.Subscriber);

        var sink = new CollectingAudioSink(target: 10);
        await using var subscriber = new MediaSubscriber(new MediaTransportOptions(), TestMedia.Transport, TestMedia.Loggers, subscriberRoom, audio: sink);
        await subscriber.StartAsync();

        await using var publisher = new MediaPublisher(new MediaTransportOptions(), TestMedia.Transport, TestMedia.Loggers, publisherRoom, audio: new ToneAudioSource());
        publisher.Start();

        Task finished = await Task.WhenAny(sink.Reached, Task.Delay(50_000));

        Assert.IsTrue(
            ReferenceEquals(finished, sink.Reached) && sink.Count >= 10,
            $"Expected >=10 decoded audio frames through the relay, got {sink.Count}.");
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task Publisher_FansOutToTwoSubscribers_OverRealRelay()
    {
        await using var relay = InProcessRelay.Start();
        var room = new RoomCode("fanout");

        await using RoomClient publisherRoom = await RoomClient.ConnectAsync(relay.WebSocketUri, room, PeerRole.Publisher);
        await using RoomClient sub1Room = await RoomClient.ConnectAsync(relay.WebSocketUri, room, PeerRole.Subscriber);
        await using RoomClient sub2Room = await RoomClient.ConnectAsync(relay.WebSocketUri, room, PeerRole.Subscriber);

        IMediaTransport transport = TestMedia.CreateCapturing(out CapturingLoggerFactory log);
        var sink1 = new CollectingAudioSink(target: 10);
        var sink2 = new CollectingAudioSink(target: 10);
        await using var sub1 = new MediaSubscriber(new MediaTransportOptions(), transport, log, sub1Room, audio: sink1);
        await using var sub2 = new MediaSubscriber(new MediaTransportOptions(), transport, log, sub2Room, audio: sink2);
        await sub1.StartAsync();
        await sub2.StartAsync();

        // One publisher fans out to both subscribers.
        await using var publisher = new MediaPublisher(new MediaTransportOptions(), transport, log, publisherRoom, audio: new ToneAudioSource());
        publisher.Start();

        _ = await Task.WhenAny(Task.WhenAll(sink1.Reached, sink2.Reached), Task.Delay(20_000));

        Assert.IsTrue(
            sink1.Count >= 10 && sink2.Count >= 10,
            $"Expected >=10 decoded audio frames at each subscriber, got {sink1.Count} and {sink2.Count}.\n=== handshake log ===\n{log.Dump()}");
    }

    /// <summary>A minimal WebSocket signaling relay over <see cref="HttpListener"/>, backed by the room router.</summary>
    private sealed class InProcessRelay : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly SignalingRouter _router = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _accept;

        private InProcessRelay(HttpListener listener, int port)
        {
            _listener = listener;
            WebSocketUri = new Uri($"ws://localhost:{port}/ws");
            _accept = Task.Run(AcceptLoopAsync);
        }

        public Uri WebSocketUri { get; }

        public static InProcessRelay Start()
        {
            int port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();
            return new InProcessRelay(listener, port);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return; // listener stopped.
                }

                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                _ = HandlePeerAsync(context);
            }
        }

        private async Task HandlePeerAsync(HttpListenerContext context)
        {
            HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
            var transport = new WebSocketSignalingTransport(wsContext.WebSocket);
            await using ISignalingSession session = _router.Connect(transport);
            transport.MessageReceived += message => session.ReceiveAsync(message).AsTask();
            try
            {
                await transport.RunAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Peer disconnected.
            }
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _listener.Close();
            try
            {
                await _accept.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Teardown race.
            }

            _cts.Dispose();
        }
    }
}
