using System.Net;
using Agash.StreamTransport.WebRtc.Ice;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;
using Agash.StreamTransport.WebRtc.Sdp;
using Agash.StreamTransport.WebRtc.Turn;

namespace Agash.StreamTransport.WebRtc;

/// <summary>A media line a <see cref="PeerConnection"/> offers: its BUNDLE mid, kind, local SSRC, and codecs.</summary>
/// <param name="Mid">The media identification tag.</param>
/// <param name="Kind">Audio or video.</param>
/// <param name="LocalSsrc">The SSRC this endpoint sends with on this line.</param>
/// <param name="Codecs">The codecs to offer, in preference order.</param>
public sealed record MediaLine(
    string Mid,
    SdpMediaKind Kind,
    uint LocalSsrc,
    IReadOnlyList<SdpCodec> Codecs
)
{
    /// <summary>
    /// The SSRC this endpoint retransmits the line's media on (RFC 4588). SRTP forbids nonce reuse, so a
    /// lost packet travels again on a distinct SSRC with a fresh sequence number, never as the original.
    /// Retransmission is offered with an <c>rtx</c> codec per media codec in <see cref="Codecs"/>
    /// (<c>apt=</c> its payload type); null offers none.
    /// </summary>
    public uint? RtxSsrc { get; init; }

    /// <summary>Whether this endpoint sends, receives or both on the line.</summary>
    public SdpDirection Direction { get; init; } = SdpDirection.SendRecv;
}

/// <summary>
/// The outcome of offer/answer for one media section: the codecs both peers agreed on, most preferred
/// first and in the offerer's payload types, and the SSRC this endpoint sends with. Each side describes
/// the codecs itself (RFC 3264): <see cref="Codecs"/> holds this endpoint's format parameters, which
/// describe what it sends, and <see cref="RemoteCodecs"/> the peer's, which describe what it sends.
/// </summary>
/// <param name="Kind">Audio or video.</param>
/// <param name="Mid">The media identification tag.</param>
/// <param name="LocalSsrc">The SSRC this endpoint sends with.</param>
/// <param name="Codecs">The agreed codecs as this endpoint described them.</param>
/// <param name="RemoteCodecs">The same codecs, in the same order, as the peer described them.</param>
public sealed record NegotiatedMediaInfo(
    SdpMediaKind Kind,
    string Mid,
    uint LocalSsrc,
    IReadOnlyList<SdpCodec> Codecs,
    IReadOnlyList<SdpCodec> RemoteCodecs
);

/// <summary>Configuration for a <see cref="PeerConnection"/>.</summary>
public sealed class PeerConnectionOptions
{
    /// <summary>The RFC 8083 circuit breakers' tunables; the RFC's defaults when null.</summary>
    public Adaptation.CircuitBreakerOptions? CircuitBreaker { get; init; }

    /// <summary>
    /// Path MTU discovery (RFC 8899): media starts at the base size and grows to what the path is found to
    /// carry, probing with padded RTX packets the peer acknowledges in its congestion-control feedback. It
    /// runs on a sending connection with a congestion controller, RTX and that feedback agreed; null keeps
    /// media at the base size of the defaults.
    /// </summary>
    public Adaptation.PathMtuOptions? PathMtu { get; init; } = new();

    /// <summary>The media lines to negotiate (offerer side); the answerer mirrors the remote offer.</summary>
    public IReadOnlyList<MediaLine> Media { get; init; } = [];

    /// <summary>
    /// The payload formats that decide whether an offered codec matches one of this endpoint's, beyond
    /// the encoding name and clock rate (an H.264 profile, say).
    /// </summary>
    public RtpPayloadFormatRegistry PayloadFormats { get; init; } =
        RtpPayloadFormatRegistry.BuiltIn;

    /// <summary>
    /// Where ICE gets its sockets; UDP on this host's addresses, ranked by
    /// <see cref="LocalAddressPreferences"/>, when null. An embedder supplies its own to put ICE on a
    /// transport of its choosing; tests supply an in-memory network.
    /// </summary>
    public IIceSocketFactory? SocketFactory { get; init; }

    /// <summary>STUN servers to gather server-reflexive candidates from.</summary>
    public IReadOnlyList<IPEndPoint> StunServers { get; init; } = [];

    /// <summary>TURN servers to allocate relayed candidates on.</summary>
    public IReadOnlyList<TurnServer> TurnServers { get; init; } = [];

    /// <summary>Which candidates ICE gathers; <see cref="IceTransportPolicy.Relay"/> sends only through TURN.</summary>
    public IceTransportPolicy IceTransportPolicy { get; init; } = IceTransportPolicy.All;

    /// <summary>Include loopback candidates (for same-host tests). Off by default.</summary>
    public bool IncludeLoopback { get; init; }

    /// <summary>
    /// Restricts ICE host-candidate gathering to local addresses matching at least one of these selectors. Each
    /// selector is a NIC name, a literal IP address, or the keyword <c>ipv4</c>/<c>ipv6</c> (case-insensitive).
    /// Empty (the default) gathers every usable address. Pinning a family on both peers forces that family for
    /// the link; pinning NIC names (e.g. the two cellular modems on an IRL field uplink) keeps gathering off
    /// unwanted interfaces such as Wi-Fi.
    /// </summary>
    public IReadOnlyList<string> LocalAddressPreferences { get; init; } = [];

    /// <summary>The one-byte header-extension id used for abs-capture-time (1-14), or 0 to disable.</summary>
    public int AbsCaptureTimeExtensionId { get; init; } = 1;

    /// <summary>
    /// Enable FlexFEC (RFC 8627) loss repair for the protected video stream: the sender emits a repair packet
    /// per <see cref="FecGroupSize"/> media packets on <see cref="FecSsrc"/>, and the receiver recovers a single
    /// lost media packet per group without a retransmit round trip. Off by default; the IRL profile turns it on.
    /// </summary>
    public bool EnableFec { get; init; }

    /// <summary>The RTP payload type for FlexFEC repair packets.</summary>
    public byte FecPayloadType { get; init; } = 35;

    /// <summary>The SSRC FlexFEC repair packets are sent on.</summary>
    public uint FecSsrc { get; init; } = 0x5EED_00F0;

    /// <summary>The media SSRC FlexFEC protects (the video stream).</summary>
    public uint FecProtectedSsrc { get; init; }

    /// <summary>Media packets protected by one repair packet (1-15).</summary>
    public int FecGroupSize { get; init; } = 10;
}

/// <summary>The aggregate connection state of a <see cref="PeerConnection"/> (ICE + DTLS).</summary>
public enum PeerConnectionState
{
    /// <summary>Created, not yet negotiating.</summary>
    New,

    /// <summary>ICE and/or DTLS in progress.</summary>
    Connecting,

    /// <summary>ICE connected and DTLS-SRTP established; media can flow.</summary>
    Connected,

    /// <summary>Connectivity or the DTLS handshake failed.</summary>
    Failed,

    /// <summary>Closed.</summary>
    Closed,
}
