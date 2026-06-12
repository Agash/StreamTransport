namespace Agash.StreamTransport;

/// <summary>
/// The peer-rendezvous seam: how a media session learns which peers to connect to and obtains a per-peer
/// <see cref="ISignalingChannel"/> for negotiation. The built-in relay-room client implements this; a host
/// can supply its own (a SignalR-hub-backed room, a fixed two-peer pairing, or any custom rendezvous) so the
/// same publisher / subscriber orchestration runs over it.
/// </summary>
/// <remarks>
/// For pure point-to-point with no rendezvous at all, skip this entirely and drive an
/// <see cref="IMediaTransport"/> sender/receiver directly with your own <see cref="ISignalingChannel"/> -
/// the room is a convenience for the multi-peer case, not a requirement of the transport.
/// </remarks>
public interface IMediaRoom : IAsyncDisposable
{
    /// <summary>This peer's id within the room.</summary>
    PeerId Self { get; }

    /// <summary>The other peers currently present, kept live as peers join and leave.</summary>
    IReadOnlyList<PeerInfo> Peers { get; }

    /// <summary>The ICE servers to use for connections in this room (STUN, plus any TURN with credentials).</summary>
    IReadOnlyList<IceServer> IceServers { get; }

    /// <summary>Raised when another peer joins.</summary>
    event Action<PeerInfo>? PeerJoined;

    /// <summary>Raised when another peer leaves.</summary>
    event Action<PeerId>? PeerLeft;

    /// <summary>
    /// Raised for each inbound application-level control message (rides the same ordered signaling link as
    /// SDP/ICE, so a control sent before an offer is observed before that offer on the far side).
    /// </summary>
    event Action<PeerControlMessage>? ControlReceived;

    /// <summary>The signaling channel for the session with <paramref name="peer"/>; created on first use.</summary>
    ISignalingChannel ChannelFor(PeerId peer);

    /// <summary>
    /// Send a control message, addressed to <paramref name="to"/> when given, otherwise fanned out to the room.
    /// </summary>
    Task SendControlAsync(string topic, string payload, PeerId? to = null, CancellationToken cancellationToken = default);
}
