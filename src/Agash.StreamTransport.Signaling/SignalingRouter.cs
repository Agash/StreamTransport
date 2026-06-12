namespace Agash.StreamTransport.Signaling;

/// <summary>
/// The default in-memory <see cref="ISignalingRouter"/>. One instance backs a whole host; bind it to a
/// transport (the relay's WebSocket endpoint or a SignalR hub) by creating one session per connection
/// via <see cref="Connect"/>. Stateless beyond the room registry, so it is safe to register as a
/// singleton.
/// </summary>
public sealed class SignalingRouter : ISignalingRouter
{
    private readonly RoomRegistry _rooms = new();
    private readonly IIceServerProvider _iceServers;

    /// <summary>Create a router. ICE servers handed to joining peers come from <paramref name="iceServers"/>.</summary>
    /// <param name="iceServers">
    /// Supplies the ICE servers advertised in each <see cref="WelcomeMessage"/>. Pass a STUN or external
    /// TURN provider; when null, peers are told no ICE servers and rely on host-candidate connectivity.
    /// </param>
    public SignalingRouter(IIceServerProvider? iceServers = null) =>
        _iceServers = iceServers ?? EmptyIceServerProvider.Instance;

    /// <inheritdoc/>
    public RoomCode CreateRoom() => _rooms.Create().Code;

    /// <inheritdoc/>
    public ISignalingSession Connect(ISignalingPeerTransport transport) =>
        new RouterSession(_rooms, _iceServers, transport);

    private sealed class EmptyIceServerProvider : IIceServerProvider
    {
        public static readonly EmptyIceServerProvider Instance = new();

        public IReadOnlyList<IceServer> GetIceServersForPeer() => [];
    }
}

/// <summary>
/// One peer's signaling session. Handles the hello handshake, then routes SDP/ICE to the addressed peer
/// and announces join/leave. The host transport feeds inbound messages through
/// <see cref="ReceiveAsync"/> and disposes the session when the connection closes.
/// </summary>
internal sealed class RouterSession(
    RoomRegistry rooms,
    IIceServerProvider iceServers,
    ISignalingPeerTransport transport) : ISignalingSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Room? _room;
    private bool _disposed;

    public PeerId? PeerId { get; private set; }

    public async ValueTask ReceiveAsync(SignalingMessage message, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            if (PeerId is null)
            {
                await HandleHelloAsync(message, cancellationToken).ConfigureAwait(false);
                return;
            }

            switch (message)
            {
                case SdpMessage sdp:
                    await RouteAsync(sdp.To, sdp with { From = PeerId }, cancellationToken).ConfigureAwait(false);
                    break;
                case IceMessage ice:
                    await RouteAsync(ice.To, ice with { From = PeerId }, cancellationToken).ConfigureAwait(false);
                    break;
                case PeerControlMessage control:
                    // Addressed control goes to the one peer; an unaddressed one fans out to the rest of the room.
                    if (control.To is not null)
                    {
                        await RouteAsync(control.To, control with { From = PeerId }, cancellationToken).ConfigureAwait(false);
                    }
                    else if (_room is not null)
                    {
                        await _room.BroadcastExceptAsync(PeerId.Value, control with { From = PeerId }, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    break;
                default:
                    // Welcome / PeerJoined / PeerLeft / a second Hello are not valid inbound from a peer.
                    break;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask HandleHelloAsync(SignalingMessage message, CancellationToken cancellationToken)
    {
        if (message is not HelloMessage hello)
        {
            await transport.SendAsync(
                new SignalingErrorMessage(SignalingErrorCode.InvalidMessage, "expected a hello message first"),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (hello.ProtocolVersion != SignalingProtocol.Version)
        {
            await transport.SendAsync(
                new SignalingErrorMessage(
                    SignalingErrorCode.VersionMismatch,
                    $"router speaks v{SignalingProtocol.Version}, client sent v{hello.ProtocolVersion}"),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // Publishers create-or-join their room; subscribers may only join an existing one.
        Room? room = hello.Role == PeerRole.Publisher
            ? rooms.GetOrCreate(hello.Room)
            : rooms.Get(hello.Room);

        if (room is null)
        {
            await transport.SendAsync(
                new SignalingErrorMessage(SignalingErrorCode.RoomNotFound, $"no room with code {hello.Room.Value}"),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        PeerId id = rooms.MintPeerId();
        PeerId = id;
        _room = room;

        var welcome = new WelcomeMessage(
            id,
            new RoomState(room.Code, room.Snapshot(), iceServers.GetIceServersForPeer()));
        await transport.SendAsync(welcome, cancellationToken).ConfigureAwait(false);

        room.Add(new Peer(id, hello.Role, transport));
        await room.BroadcastExceptAsync(id, new PeerJoinedMessage(new PeerInfo(id, hello.Role)), cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask RouteAsync(PeerId? target, SignalingMessage message, CancellationToken cancellationToken)
    {
        if (target is null || _room is null)
        {
            return;
        }

        ISignalingPeerTransport? targetTransport = _room.TransportFor(target.Value);
        if (targetTransport is null)
        {
            return; // raced disconnect or a spoofed target; drop.
        }

        try
        {
            await targetTransport.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Target link is broken; its own session will clean up.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (PeerId is { } id && _room is { } room)
            {
                room.Remove(id);
                await room.BroadcastExceptAsync(id, new PeerLeftMessage(id), CancellationToken.None).ConfigureAwait(false);
                rooms.RemoveIfEmpty(room.Code);
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
