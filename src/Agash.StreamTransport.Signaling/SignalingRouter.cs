using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.Signaling;

/// <summary>
/// The default in-memory <see cref="ISignalingRouter"/>. One instance backs a whole host; bind it to a
/// transport (the relay's WebSocket endpoint or a SignalR hub) by creating one session per connection
/// via <see cref="Connect"/>. Stateless beyond the room registry, so it is safe to register as a
/// singleton.
/// </summary>
public sealed class SignalingRouter : ISignalingRouter
{
    private readonly RoomRegistry _rooms;
    private readonly IIceServerProvider _iceServers;
    private readonly ILogger _logger;
    private readonly SignalingMetrics _metrics;

    /// <summary>Create a router. ICE servers handed to joining peers come from <paramref name="iceServers"/>.</summary>
    /// <param name="iceServers">
    /// Supplies the ICE servers advertised in each <see cref="WelcomeMessage"/>. Pass a STUN or external
    /// TURN provider; when null, peers are told no ICE servers and rely on host-candidate connectivity.
    /// </param>
    /// <param name="loggerFactory">The logging; none when null.</param>
    /// <param name="meterFactory">
    /// Where the metrics' meter comes from (<see cref="SignalingDiagnostics.MeterName"/>); a meter of the
    /// router's own when null.
    /// </param>
    public SignalingRouter(
        IIceServerProvider? iceServers = null,
        ILoggerFactory? loggerFactory = null,
        IMeterFactory? meterFactory = null
    )
    {
        _iceServers = iceServers ?? EmptyIceServerProvider.Instance;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SignalingRouter>();
        _metrics = new SignalingMetrics(meterFactory);
        _rooms = new RoomRegistry(_logger, _metrics);
    }

    /// <inheritdoc/>
    public RoomCode CreateRoom() => _rooms.Create().Code;

    /// <inheritdoc/>
    public ISignalingSession Connect(ISignalingPeerTransport transport) =>
        new RouterSession(_rooms, _iceServers, transport, _logger, _metrics);

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
internal sealed partial class RouterSession(
    RoomRegistry rooms,
    IIceServerProvider iceServers,
    ISignalingPeerTransport transport,
    ILogger logger,
    SignalingMetrics metrics
) : ISignalingSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Room? _room;
    private PeerRole _role;
    private bool _disposed;

    public PeerId? PeerId { get; private set; }

    public async ValueTask ReceiveAsync(
        SignalingMessage message,
        CancellationToken cancellationToken = default
    )
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
                    await RouteAsync(sdp.To, sdp with { From = PeerId }, "sdp", cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case IceMessage ice:
                    await RouteAsync(ice.To, ice with { From = PeerId }, "ice", cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case PeerControlMessage control:
                    // Addressed control goes to the one peer; an unaddressed one fans out to the rest of the room.
                    if (control.To is not null)
                    {
                        await RouteAsync(
                                control.To,
                                control with
                                {
                                    From = PeerId,
                                },
                                "control",
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                    }
                    else if (_room is not null)
                    {
                        await _room
                            .BroadcastExceptAsync(
                                PeerId.Value,
                                control with
                                {
                                    From = PeerId,
                                },
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                    }

                    break;
                default:
                    // Welcome / PeerJoined / PeerLeft / a second Hello are not valid inbound from a peer.
                    metrics.MessagesDropped.Add(1, SignalingMetrics.Reason("invalid"));
                    LogInvalidMessage(PeerId.Value, message.GetType().Name);
                    break;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask HandleHelloAsync(
        SignalingMessage message,
        CancellationToken cancellationToken
    )
    {
        if (message is not HelloMessage hello)
        {
            metrics.JoinsRefused.Add(1, SignalingMetrics.Reason("invalid"));
            LogJoinRefused("the first message was not a hello");
            await transport
                .SendAsync(
                    new SignalingErrorMessage(
                        SignalingErrorCode.InvalidMessage,
                        "expected a hello message first"
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }

        if (hello.ProtocolVersion != SignalingProtocol.Version)
        {
            metrics.JoinsRefused.Add(1, SignalingMetrics.Reason("version"));
            LogJoinRefused(
                $"protocol v{hello.ProtocolVersion}, router v{SignalingProtocol.Version}"
            );
            await transport
                .SendAsync(
                    new SignalingErrorMessage(
                        SignalingErrorCode.VersionMismatch,
                        $"router speaks v{SignalingProtocol.Version}, client sent v{hello.ProtocolVersion}"
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }

        // Publishers create-or-join their room; subscribers may only join an existing one.
        Room? room =
            hello.Role == PeerRole.Publisher
                ? rooms.GetOrCreate(hello.Room)
                : rooms.Get(hello.Room);

        if (room is null)
        {
            metrics.JoinsRefused.Add(1, SignalingMetrics.Reason("room_not_found"));
            LogJoinRefused("no such room");
            LogUnknownRoom(hello.Room.Value);
            await transport
                .SendAsync(
                    new SignalingErrorMessage(
                        SignalingErrorCode.RoomNotFound,
                        $"no room with code {hello.Room.Value}"
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }

        PeerId id = rooms.MintPeerId();
        PeerId = id;
        _room = room;

        var welcome = new WelcomeMessage(
            id,
            new RoomState(room.Code, room.Snapshot(), iceServers.GetIceServersForPeer())
        );
        await transport.SendAsync(welcome, cancellationToken).ConfigureAwait(false);

        room.Add(new Peer(id, hello.Role, transport));
        _role = hello.Role;
        metrics.PeersActive.Add(1, SignalingMetrics.Role(hello.Role));
        LogJoined(id, hello.Role);
        LogJoinedRoom(id, room.Code.Value);
        await room.BroadcastExceptAsync(
                id,
                new PeerJoinedMessage(new PeerInfo(id, hello.Role)),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async ValueTask RouteAsync(
        PeerId? target,
        SignalingMessage message,
        string type,
        CancellationToken cancellationToken
    )
    {
        if (target is null || _room is null)
        {
            metrics.MessagesDropped.Add(1, SignalingMetrics.Reason("no_target"));
            return;
        }

        ISignalingPeerTransport? targetTransport = _room.TransportFor(target.Value);
        if (targetTransport is null)
        {
            // A disconnect that raced the message, or a peer naming one that is not in its room.
            metrics.MessagesDropped.Add(1, SignalingMetrics.Reason("no_target"));
            LogNoTarget(PeerId!.Value, target.Value, type);
            return;
        }

        try
        {
            await targetTransport.SendAsync(message, cancellationToken).ConfigureAwait(false);
            metrics.MessagesRouted.Add(1, SignalingMetrics.Type(type));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The target's link is broken; its own session cleans up when its connection ends.
            metrics.MessagesDropped.Add(1, SignalingMetrics.Reason("link_broken"));
            LogForwardFailed(exception, PeerId!.Value, target.Value, type);
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
                metrics.PeersActive.Add(-1, SignalingMetrics.Role(_role));
                LogLeft(id);
                await room.BroadcastExceptAsync(id, new PeerLeftMessage(id), CancellationToken.None)
                    .ConfigureAwait(false);
                rooms.RemoveIfEmpty(room.Code);
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    [LoggerMessage(
        EventId = 2800,
        Level = LogLevel.Information,
        Message = "Peer {PeerId} joined a room as {Role}."
    )]
    private partial void LogJoined(PeerId peerId, PeerRole role);

    [LoggerMessage(
        EventId = 2801,
        Level = LogLevel.Debug,
        Message = "Peer {PeerId} is in room {Room}."
    )]
    private partial void LogJoinedRoom(PeerId peerId, string room);

    [LoggerMessage(
        EventId = 2802,
        Level = LogLevel.Information,
        Message = "Peer {PeerId} left its room."
    )]
    private partial void LogLeft(PeerId peerId);

    [LoggerMessage(
        EventId = 2803,
        Level = LogLevel.Warning,
        Message = "A join was refused: {Reason}."
    )]
    private partial void LogJoinRefused(string reason);

    [LoggerMessage(
        EventId = 2804,
        Level = LogLevel.Debug,
        Message = "A join named room {Room}, which does not exist."
    )]
    private partial void LogUnknownRoom(string room);

    [LoggerMessage(
        EventId = 2805,
        Level = LogLevel.Debug,
        Message = "Peer {PeerId} sent {Type} for peer {Target}, which is not in its room; dropped."
    )]
    private partial void LogNoTarget(PeerId peerId, PeerId target, string type);

    [LoggerMessage(
        EventId = 2806,
        Level = LogLevel.Debug,
        Message = "Forwarding {Type} from peer {PeerId} to peer {Target} failed; its session ends with its connection."
    )]
    private partial void LogForwardFailed(
        Exception exception,
        PeerId peerId,
        PeerId target,
        string type
    );

    [LoggerMessage(
        EventId = 2807,
        Level = LogLevel.Warning,
        Message = "Peer {PeerId} sent {MessageType}, which only the router sends; dropped."
    )]
    private partial void LogInvalidMessage(PeerId peerId, string messageType);
}
