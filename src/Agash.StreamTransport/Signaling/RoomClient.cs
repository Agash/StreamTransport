using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Agash.StreamTransport;

/// <summary>
/// The client end of the room protocol. Connects to a relay (or a host's own hub) over one duplex
/// link, performs the hello/welcome handshake, surfaces peer join/leave, and multiplexes the single link
/// into a per-peer <see cref="ISignalingChannel"/>. That lets the ordinary <see cref="WebRtcMediaSender"/>
/// and <see cref="WebRtcMediaReceiver"/> drive one peer connection each, unchanged - a publisher creates
/// a sender per subscriber that joins, a subscriber creates a receiver for the publisher.
/// </summary>
public sealed class RoomClient : IMediaRoom
{
    private readonly IDuplexSignalingTransport _transport;
    private readonly bool _ownsTransport;
    private readonly ConcurrentDictionary<long, PeerSignalingChannel> _channels = new();
    private readonly ConcurrentDictionary<long, PeerInfo> _peers = new();
    private readonly TaskCompletionSource<WelcomeMessage> _welcome =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private Task? _pump;

    private RoomClient(IDuplexSignalingTransport transport, bool ownsTransport)
    {
        _transport = transport;
        _ownsTransport = ownsTransport;
        _transport.MessageReceived += OnMessageAsync;
    }

    /// <summary>This peer's id, assigned by the router on welcome.</summary>
    public PeerId Self { get; private set; }

    /// <summary>The room state from welcome: existing peers and the ICE servers to use.</summary>
    public RoomState State { get; private set; } = new(default, [], []);

    /// <summary>The ICE servers the router provisioned for this peer (STUN, plus any TURN with credentials).</summary>
    public IReadOnlyList<IceServer> IceServers => State.IceServers;

    /// <summary>Raised when another peer joins the room.</summary>
    public event Action<PeerInfo>? PeerJoined;

    /// <summary>Raised when another peer leaves the room.</summary>
    public event Action<PeerId>? PeerLeft;

    /// <summary>
    /// Raised for each inbound <see cref="PeerControlMessage"/> - the generic application-level channel that
    /// rides the same signaling link as SDP/ICE. <see cref="PeerControlMessage.From"/> is the sender. A host
    /// can use this for arbitrary inter-client messaging; the media layer uses it to negotiate
    /// per-stream options (e.g. <c>stream.alpha</c>) before the offer.
    /// </summary>
    public event Action<PeerControlMessage>? ControlReceived;

    /// <summary>
    /// Connect to a relay's WebSocket signaling endpoint, join <paramref name="room"/> as
    /// <paramref name="role"/>, and complete the welcome handshake. Convenience over
    /// <see cref="ConnectAsync(IDuplexSignalingTransport, RoomCode, PeerRole, CancellationToken)"/> for the
    /// common case; the client owns and closes the socket.
    /// </summary>
    public static async Task<RoomClient> ConnectAsync(
        Uri relayWebSocketUrl, RoomCode room, PeerRole role, CancellationToken cancellationToken = default)
    {
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(relayWebSocketUrl, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var transport = new WebSocketSignalingTransport(socket, ownsSocket: true);
        var client = new RoomClient(transport, ownsTransport: true);
        return await client.StartAsync(room, role, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Join <paramref name="room"/> as <paramref name="role"/> over an already-connected duplex signaling
    /// transport, and complete the welcome handshake. Use this to bind the room client to a non-WebSocket
    /// link - for example plugging in a SignalR connection. The caller owns the transport's
    /// lifetime; disposing this client does not dispose the supplied transport.
    /// </summary>
    public static Task<RoomClient> ConnectAsync(
        IDuplexSignalingTransport transport, RoomCode room, PeerRole role, CancellationToken cancellationToken = default)
    {
        var client = new RoomClient(transport, ownsTransport: false);
        return client.StartAsync(room, role, cancellationToken);
    }

    private async Task<RoomClient> StartAsync(RoomCode room, PeerRole role, CancellationToken cancellationToken)
    {
        _pump = _transport.RunAsync(_cts.Token);
        await _transport.SendAsync(new HelloMessage(SignalingProtocol.Version, role, room), cancellationToken)
            .ConfigureAwait(false);

        using CancellationTokenRegistration _ = cancellationToken.Register(
            static state => ((TaskCompletionSource<WelcomeMessage>)state!).TrySetCanceled(), _welcome);
        WelcomeMessage welcome = await _welcome.Task.ConfigureAwait(false);
        Self = welcome.PeerId;
        State = welcome.RoomState;
        foreach (PeerInfo peer in welcome.RoomState.Peers)
        {
            _peers[peer.PeerId.Value] = peer;
        }

        return this;
    }

    /// <summary>
    /// The peers currently in the room (other than this one), kept live as peers join and leave. Prefer
    /// this over <see cref="State"/>.<see cref="RoomState.Peers"/>, which is only the snapshot from join.
    /// </summary>
    public IReadOnlyList<PeerInfo> Peers => [.. _peers.Values];

    /// <summary>
    /// The signaling channel for the WebRTC session with <paramref name="peer"/>. Created on first use;
    /// pass it to a <see cref="WebRtcMediaSender"/> or <see cref="WebRtcMediaReceiver"/>.
    /// </summary>
    public ISignalingChannel ChannelFor(PeerId peer) =>
        _channels.GetOrAdd(peer.Value, static (id, self) => new PeerSignalingChannel(self, new PeerId(id)), this);

    internal ValueTask SendToAsync(PeerId target, SignalingMessage message, CancellationToken cancellationToken) =>
        _transport.SendAsync(message, cancellationToken);

    /// <summary>
    /// Send a generic control message over the signaling link. Addressed to <paramref name="to"/> when given,
    /// otherwise fanned out to the rest of the room by the router. Ordered relative to SDP/ICE on the same
    /// link, so a control message sent before an offer is observed before that offer on the far side.
    /// </summary>
    public Task SendControlAsync(string topic, string payload, PeerId? to = null, CancellationToken cancellationToken = default) =>
        _transport.SendAsync(new PeerControlMessage(topic, payload, To: to), cancellationToken).AsTask();

    private async Task OnMessageAsync(SignalingMessage message)
    {
        switch (message)
        {
            case WelcomeMessage welcome:
                _welcome.TrySetResult(welcome);
                break;
            case PeerJoinedMessage joined:
                _peers[joined.Peer.PeerId.Value] = joined.Peer;
                PeerJoined?.Invoke(joined.Peer);
                break;
            case PeerLeftMessage left:
                _peers.TryRemove(left.PeerId.Value, out _);
                if (_channels.TryRemove(left.PeerId.Value, out PeerSignalingChannel? channel))
                {
                    await channel.DisposeAsync().ConfigureAwait(false);
                }

                PeerLeft?.Invoke(left.PeerId);
                break;
            case SdpMessage { From: { } from } sdp:
                await ((PeerSignalingChannel)ChannelFor(from))
                    .DispatchDescriptionAsync(new SessionDescription(sdp.Kind, sdp.Sdp)).ConfigureAwait(false);
                break;
            case IceMessage { From: { } from } ice:
                await ((PeerSignalingChannel)ChannelFor(from))
                    .DispatchIceAsync(new IceCandidate(ice.Candidate, ice.SdpMid, ice.SdpMLineIndex))
                    .ConfigureAwait(false);
                break;
            case PeerControlMessage control:
                ControlReceived?.Invoke(control);
                break;
            case SignalingErrorMessage error:
                _welcome.TrySetException(
                    new InvalidOperationException($"Signaling error {error.Code}: {error.Detail}"));
                break;
            default:
                break;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _transport.MessageReceived -= OnMessageAsync;
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Pump teardown races are benign.
            }
        }

        // Only dispose the transport (and its socket) when we created it; a caller-supplied transport
        // (e.g. a host's SignalR link) is the caller's to dispose.
        if (_ownsTransport)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }

        _cts.Dispose();
    }
}

/// <summary>
/// A per-peer <see cref="ISignalingChannel"/> over a <see cref="RoomClient"/>: outbound SDP/ICE are
/// addressed to the remote peer; inbound messages are delivered by the room client routing on the
/// sender's id.
/// </summary>
internal sealed class PeerSignalingChannel(RoomClient room, PeerId peer) : ISignalingChannel
{
    private readonly Lock _gate = new();
    private readonly Queue<IceCandidate> _pendingIce = new();
    private Func<SessionDescription, Task>? _descriptionReceived;
    private Func<IceCandidate, Task>? _iceReceived;
    private SessionDescription? _pendingDescription;

    // The remote offer (or its ICE) can arrive before the media session attaches its handlers - the
    // publisher offers the moment the subscriber joins. Buffer until a handler subscribes, then flush, so
    // negotiation is independent of which side wires up first.
    public event Func<SessionDescription, Task>? DescriptionReceived
    {
        add
        {
            SessionDescription? flush;
            lock (_gate)
            {
                _descriptionReceived += value;
                flush = _pendingDescription;
                _pendingDescription = null;
            }

            if (flush is { } description && value is not null)
            {
                _ = value(description);
            }
        }
        remove
        {
            lock (_gate)
            {
                _descriptionReceived -= value;
            }
        }
    }

    public event Func<IceCandidate, Task>? IceCandidateReceived
    {
        add
        {
            IceCandidate[] flush;
            lock (_gate)
            {
                _iceReceived += value;
                flush = [.. _pendingIce];
                _pendingIce.Clear();
            }

            if (value is not null)
            {
                foreach (IceCandidate candidate in flush)
                {
                    _ = value(candidate);
                }
            }
        }
        remove
        {
            lock (_gate)
            {
                _iceReceived -= value;
            }
        }
    }

    public Task SendAsync(SessionDescription description, CancellationToken cancellationToken = default) =>
        room.SendToAsync(peer, new SdpMessage(description.Kind, description.Sdp, To: peer), cancellationToken).AsTask();

    public Task SendAsync(IceCandidate candidate, CancellationToken cancellationToken = default) =>
        room.SendToAsync(
            peer,
            new IceMessage(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex, To: peer),
            cancellationToken).AsTask();

    internal Task DispatchDescriptionAsync(SessionDescription description)
    {
        Func<SessionDescription, Task>? handler;
        lock (_gate)
        {
            handler = _descriptionReceived;
            if (handler is null)
            {
                _pendingDescription = description;
            }
        }

        return handler is null ? Task.CompletedTask : handler(description);
    }

    internal Task DispatchIceAsync(IceCandidate candidate)
    {
        Func<IceCandidate, Task>? handler;
        lock (_gate)
        {
            handler = _iceReceived;
            if (handler is null)
            {
                _pendingIce.Enqueue(candidate);
            }
        }

        return handler is null ? Task.CompletedTask : handler(candidate);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
