using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agash.StreamTransport;

/// <summary>
/// Wire-protocol constants shared by every signaling participant: the room-aware client, the signaling
/// router, and any external relay. Bumping <see cref="Version"/> means coordinating relay and client
/// deploys, so keep the message set additive.
/// </summary>
public static class SignalingProtocol
{
    /// <summary>The signaling protocol version. A peer whose version differs from the router's is rejected.</summary>
    public const int Version = 1;
}

/// <summary>The role a peer plays in a room.</summary>
public enum PeerRole
{
    /// <summary>Publishes a media stream into the room (the sending agent).</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("publisher")]
    Publisher,

    /// <summary>Consumes streams from the room (a receiving agent or browser).</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("subscriber")]
    Subscriber,
}

/// <summary>A connection-level signaling error reported by the router; usually fatal for that peer.</summary>
public enum SignalingErrorCode
{
    /// <summary>The peer's protocol version does not match the router's.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("versionMismatch")]
    VersionMismatch,

    /// <summary>The room has reached its peer limit.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("roomFull")]
    RoomFull,

    /// <summary>No room exists with the requested code.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("roomNotFound")]
    RoomNotFound,

    /// <summary>A message could not be parsed or was not valid for the current state.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("invalidMessage")]
    InvalidMessage,

    /// <summary>An unexpected server-side failure.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("internal")]
    Internal,
}

/// <summary>
/// A short, human-friendly room identifier (the router mints these and clients pass them around).
/// Serializes as a bare JSON string so browser clients can read it directly.
/// </summary>
[JsonConverter(typeof(RoomCodeJsonConverter))]
public readonly record struct RoomCode(string Value)
{
    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>An opaque peer identifier minted by the router on join. Serializes as a bare JSON number.</summary>
[JsonConverter(typeof(PeerIdJsonConverter))]
public readonly record struct PeerId(long Value)
{
    /// <inheritdoc/>
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A snapshot of one peer in a room.</summary>
/// <param name="PeerId">The peer's id.</param>
/// <param name="Role">The peer's role.</param>
public readonly record struct PeerInfo(PeerId PeerId, PeerRole Role);

/// <summary>
/// An ICE server a peer should use, shaped to mirror the W3C <c>RTCIceServer</c> dictionary so browser
/// clients can pass it straight into <c>new RTCPeerConnection({ iceServers })</c>. STUN entries omit the
/// credentials; TURN entries carry the ephemeral username/credential pair.
/// </summary>
/// <param name="Urls">One or more ICE URLs (e.g. <c>stun:host:3478</c>, <c>turn:host:3478?transport=udp</c>).</param>
/// <param name="Username">TURN username, or null for STUN.</param>
/// <param name="Credential">TURN credential, or null for STUN.</param>
public sealed record IceServer(
    IReadOnlyList<string> Urls,
    string? Username = null,
    string? Credential = null);

/// <summary>
/// The room state the router hands a peer on join: who else is present and which ICE servers to use
/// (STUN, plus any relay-provided TURN with ephemeral credentials).
/// </summary>
/// <param name="Code">The room code.</param>
/// <param name="Peers">The peers already in the room.</param>
/// <param name="IceServers">The ICE servers this peer should configure.</param>
public sealed record RoomState(
    RoomCode Code,
    IReadOnlyList<PeerInfo> Peers,
    IReadOnlyList<IceServer> IceServers);

/// <summary>
/// Supplies the ICE servers a joining peer should use. The STUN-only implementation advertises the
/// host's STUN endpoint; a future TURN implementation mints ephemeral credentials per peer.
/// </summary>
public interface IIceServerProvider
{
    /// <summary>Build the ICE-server list for a peer that is joining a room.</summary>
    IReadOnlyList<IceServer> GetIceServersForPeer();
}

/// <summary>
/// Base type for every message on the signaling wire. The same set flows in both directions; some
/// variants are only ever sent one way in practice (the router sends <see cref="WelcomeMessage"/>,
/// clients send <see cref="HelloMessage"/>). Polymorphic JSON uses a <c>type</c> discriminator so the
/// shape is identical across the WebSocket relay and a SignalR hub.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(SdpMessage), "sdp")]
[JsonDerivedType(typeof(IceMessage), "ice_candidate")]
[JsonDerivedType(typeof(PeerJoinedMessage), "peer_joined")]
[JsonDerivedType(typeof(PeerLeftMessage), "peer_left")]
[JsonDerivedType(typeof(PeerControlMessage), "peer_control")]
[JsonDerivedType(typeof(SignalingErrorMessage), "error")]
public abstract record SignalingMessage;

/// <summary>First message a peer sends; the router validates the version and either joins or rejects.</summary>
/// <param name="ProtocolVersion">The client's <see cref="SignalingProtocol.Version"/>.</param>
/// <param name="Role">The role the peer wants to play.</param>
/// <param name="Room">The room code to join.</param>
public sealed record HelloMessage(int ProtocolVersion, PeerRole Role, RoomCode Room) : SignalingMessage;

/// <summary>The router's acknowledgement of <see cref="HelloMessage"/>: the assigned id and room state.</summary>
/// <param name="PeerId">The id the router minted for this peer.</param>
/// <param name="RoomState">A snapshot of the room and the ICE servers to use.</param>
public sealed record WelcomeMessage(PeerId PeerId, RoomState RoomState) : SignalingMessage;

/// <summary>
/// An SDP offer or answer routed peer-to-peer through the router. The sender sets <see cref="To"/>; the
/// router stamps <see cref="From"/> from the originating connection so it cannot be spoofed.
/// </summary>
/// <param name="Kind">Offer or answer.</param>
/// <param name="Sdp">The SDP payload.</param>
/// <param name="From">Set by the router when forwarding; null when a client sends.</param>
/// <param name="To">Set by the client; the router routes by this field.</param>
public sealed record SdpMessage(SdpKind Kind, string Sdp, PeerId? From = null, PeerId? To = null) : SignalingMessage;

/// <summary>A trickled ICE candidate routed peer-to-peer through the router.</summary>
/// <param name="Candidate">The candidate line.</param>
/// <param name="SdpMid">The media stream identification, if any.</param>
/// <param name="SdpMLineIndex">The media line index, if any.</param>
/// <param name="From">Set by the router when forwarding; null when a client sends.</param>
/// <param name="To">Set by the client; the router routes by this field.</param>
public sealed record IceMessage(
    string Candidate, string? SdpMid, int? SdpMLineIndex, PeerId? From = null, PeerId? To = null) : SignalingMessage;

/// <summary>The router notifies the other peers when someone joins.</summary>
/// <param name="Peer">The peer that joined.</param>
public sealed record PeerJoinedMessage(PeerInfo Peer) : SignalingMessage;

/// <summary>The router notifies the other peers when someone leaves.</summary>
/// <param name="PeerId">The peer that left.</param>
public sealed record PeerLeftMessage(PeerId PeerId) : SignalingMessage;

/// <summary>
/// A generic application-level message routed between peers through the signaling link, alongside SDP/ICE.
/// The transport itself never interprets <see cref="Topic"/> or <see cref="Payload"/> - they are an opaque
/// contract between the peers - so this is the reusable host-to-host channel: a host application can carry
/// its own inter-client messages over the same relay/SignalR connection, and the media layer uses it for
/// pre-offer negotiation (e.g. <c>stream.alpha</c>). Delivery is ordered relative to SDP/ICE on the same
/// link, so a control message sent before an offer is observed before that offer on the far side.
/// </summary>
/// <param name="Topic">A namespaced message kind the peers agree on (e.g. <c>"stream.alpha"</c>).</param>
/// <param name="Payload">An opaque, caller-encoded payload (a scalar, or JSON for structured data).</param>
/// <param name="From">Set by the router when forwarding; null when a client sends.</param>
/// <param name="To">Target peer set by the client; when null the router fans the message out to the room.</param>
public sealed record PeerControlMessage(string Topic, string Payload, PeerId? From = null, PeerId? To = null)
    : SignalingMessage;

/// <summary>A connection-level error from the router.</summary>
/// <param name="Code">The error code.</param>
/// <param name="Detail">A human-readable detail.</param>
public sealed record SignalingErrorMessage(SignalingErrorCode Code, string Detail) : SignalingMessage;

/// <summary>
/// One peer's outbound link as seen by the router: the router calls this to push a message down to that
/// peer's transport (a WebSocket frame, a SignalR client call, etc.). Implemented by the host transport
/// adapter, not by the transport library.
/// </summary>
public interface ISignalingPeerTransport
{
    /// <summary>Send a message to this peer.</summary>
    ValueTask SendAsync(SignalingMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// A full-duplex signaling link to a router, from the client's side: send messages out, receive messages
/// in, and pump until closed. The room-aware client (<c>RoomClient</c>) is built on this, so the link can
/// be a plain WebSocket, a SignalR connection, or anything else a host already has - a host can, for
/// example, implement this over an existing SignalR hub instead of opening a separate WebSocket.
/// </summary>
public interface IDuplexSignalingTransport : ISignalingPeerTransport, IAsyncDisposable
{
    /// <summary>Raised for each inbound signaling message from the router.</summary>
    event Func<SignalingMessage, Task>? MessageReceived;

    /// <summary>Pump inbound messages until the link closes or the token is cancelled.</summary>
    Task RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A live signaling session for one connected peer. The host transport adapter creates it via
/// <see cref="ISignalingRouter.Connect"/>, feeds inbound messages through <see cref="ReceiveAsync"/>,
/// and disposes it when the underlying connection closes.
/// </summary>
public interface ISignalingSession : IAsyncDisposable
{
    /// <summary>The peer's id once it has completed the hello handshake, or null before then.</summary>
    PeerId? PeerId { get; }

    /// <summary>Hand an inbound message from this peer's transport to the router for processing/routing.</summary>
    ValueTask ReceiveAsync(SignalingMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// The transport-agnostic signaling broker: it owns the room registry, mints peer ids, routes SDP/ICE
/// between peers in a room, and announces joins/leaves. A host binds it to a concrete transport (the
/// relay's WebSocket endpoint, or a host's SignalR hub) by implementing
/// <see cref="ISignalingPeerTransport"/> per connection. Join rule: a <see cref="PeerRole.Publisher"/>
/// creates the room if its code is unknown (so a publisher owns its room and survives reconnects); a
/// <see cref="PeerRole.Subscriber"/> joining an unknown code is rejected with
/// <see cref="SignalingErrorCode.RoomNotFound"/>.
/// </summary>
public interface ISignalingRouter
{
    /// <summary>
    /// Pre-mint an empty room and return its code, for a host that wants to display the code before the
    /// publisher connects. Optional: publishers also create their room implicitly on join.
    /// </summary>
    RoomCode CreateRoom();

    /// <summary>
    /// Begin a signaling session for a newly connected peer. The returned session accepts inbound
    /// messages via <see cref="ISignalingSession.ReceiveAsync"/> and pushes outbound messages through
    /// <paramref name="transport"/>.
    /// </summary>
    ISignalingSession Connect(ISignalingPeerTransport transport);
}

/// <summary>Serializes <see cref="RoomCode"/> as a bare JSON string.</summary>
internal sealed class RoomCodeJsonConverter : JsonConverter<RoomCode>
{
    public override RoomCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, RoomCode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

/// <summary>Serializes <see cref="PeerId"/> as a bare JSON number.</summary>
internal sealed class PeerIdJsonConverter : JsonConverter<PeerId>
{
    public override PeerId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetInt64());

    public override void Write(Utf8JsonWriter writer, PeerId value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.Value);
}
