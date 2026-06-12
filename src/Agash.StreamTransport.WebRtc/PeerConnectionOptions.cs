using System.Net;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc;

/// <summary>A media line a <see cref="PeerConnection"/> offers: its BUNDLE mid, kind, local SSRC, and codecs.</summary>
/// <param name="Mid">The media identification tag.</param>
/// <param name="Kind">Audio or video.</param>
/// <param name="LocalSsrc">The SSRC this endpoint sends with on this line.</param>
/// <param name="Codecs">The codecs to offer, in preference order.</param>
public sealed record MediaLine(string Mid, SdpMediaKind Kind, uint LocalSsrc, IReadOnlyList<SdpCodec> Codecs)
{
    /// <summary>
    /// The RTX SSRC for retransmissions of this line (RFC 4588). Required to serve NACKs: SRTP forbids
    /// nonce reuse, so a lost packet must be retransmitted on a distinct SSRC with a fresh sequence number,
    /// never by resending the original. Null disables retransmission for this line.
    /// </summary>
    public uint? RtxSsrc { get; init; }

    /// <summary>The RTX payload type (RFC 4588) used for retransmissions of this line.</summary>
    public byte? RtxPayloadType { get; init; }
}

/// <summary>
/// The outcome of offer/answer for one media section: the codec(s) both peers agreed on (in the offerer's
/// payload-type space) plus the local SSRC this endpoint sends that media with. The media layer reads this
/// to pick the negotiated encoder/decoder + payload type, rather than assuming a fixed codec.
/// </summary>
public sealed record NegotiatedMediaInfo(SdpMediaKind Kind, string Mid, uint LocalSsrc, IReadOnlyList<SdpCodec> Codecs);

/// <summary>Configuration for a <see cref="PeerConnection"/>.</summary>
public sealed class PeerConnectionOptions
{
    /// <summary>The media lines to negotiate (offerer side); the answerer mirrors the remote offer.</summary>
    public IReadOnlyList<MediaLine> Media { get; init; } = [];

    /// <summary>STUN servers to gather server-reflexive candidates from.</summary>
    public IReadOnlyList<IPEndPoint> StunServers { get; init; } = [];

    /// <summary>Include loopback candidates (for same-host tests). Off by default.</summary>
    public bool IncludeLoopback { get; init; }

    /// <summary>The one-byte header-extension id used for abs-capture-time (1–14), or 0 to disable.</summary>
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

    /// <summary>Media packets protected by one repair packet (1–15).</summary>
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
