using Dtls.Core;

namespace Agash.StreamTransport.WebRtc.Sdp;

/// <summary>Whether a session description is an offer or an answer (JSEP, RFC 8829).</summary>
public enum SdpType
{
    /// <summary>An offer.</summary>
    Offer,

    /// <summary>An answer.</summary>
    Answer,
}

/// <summary>The kind of media in an <c>m=</c> section.</summary>
public enum SdpMediaKind
{
    /// <summary>Audio.</summary>
    Audio,

    /// <summary>Video.</summary>
    Video,

    /// <summary>A data channel (application).</summary>
    Application,
}

/// <summary>The direction of a media section (RFC 8866 §6.7).</summary>
public enum SdpDirection
{
    /// <summary>Send and receive.</summary>
    SendRecv,

    /// <summary>Send only.</summary>
    SendOnly,

    /// <summary>Receive only.</summary>
    RecvOnly,

    /// <summary>Neither send nor receive.</summary>
    Inactive,
}

/// <summary>
/// The DTLS role advertised by <c>a=setup</c> (RFC 4145, RFC 8842 section 5.1). <c>holdconn</c> is not a
/// value here: DTLS never uses it.
/// </summary>
public enum SdpSetup
{
    /// <summary>actpass - offer the choice (offers).</summary>
    ActPass,

    /// <summary>active - be the DTLS client.</summary>
    Active,

    /// <summary>passive - be the DTLS server.</summary>
    Passive,
}

/// <summary>A negotiated codec in an <c>m=</c> section (<c>a=rtpmap</c> / <c>a=fmtp</c> / <c>a=rtcp-fb</c>).</summary>
/// <param name="PayloadType">The RTP payload type.</param>
/// <param name="EncodingName">The encoding name, e.g. <c>opus</c>, <c>H264</c>, <c>H265</c>.</param>
/// <param name="ClockRate">The RTP clock rate in Hz.</param>
/// <param name="Channels">The channel count (audio only), or null.</param>
/// <param name="FormatParameters">The <c>a=fmtp</c> parameter string, or null.</param>
/// <param name="RtcpFeedback">The <c>a=rtcp-fb</c> values advertised for this codec (e.g. <c>nack</c>, <c>nack pli</c>).</param>
public readonly record struct SdpCodec(
    int PayloadType,
    string EncodingName,
    int ClockRate,
    int? Channels,
    string? FormatParameters,
    IReadOnlyList<string> RtcpFeedback
);

/// <summary>
/// One <c>m=</c> media section of a WebRTC session description, carrying the ICE credentials, DTLS
/// fingerprint + setup role, codecs, and the BUNDLE/rtcp-mux markers a browser requires (JSEP §5.2).
/// </summary>
public sealed record SdpMediaDescription
{
    /// <summary>The media kind.</summary>
    public required SdpMediaKind Kind { get; init; }

    /// <summary>The media stream identification tag (<c>a=mid</c>), also the BUNDLE tag.</summary>
    public required string Mid { get; init; }

    /// <summary>The direction.</summary>
    public SdpDirection Direction { get; init; } = SdpDirection.SendRecv;

    /// <summary>
    /// Whether the section is rejected: port 0 on its m-line, as an answerer declines a section it
    /// shares no codec with (RFC 3264 section 6). A rejected section carries no media.
    /// </summary>
    public bool Rejected { get; init; }

    /// <summary>The offered/answered codecs in preference order.</summary>
    public required IReadOnlyList<SdpCodec> Codecs { get; init; }

    /// <summary>The ICE username fragment (<c>a=ice-ufrag</c>).</summary>
    public required string IceUfrag { get; init; }

    /// <summary>The ICE password (<c>a=ice-pwd</c>).</summary>
    public required string IcePwd { get; init; }

    /// <summary>
    /// The DTLS certificate fingerprints (<c>a=fingerprint</c>, RFC 8122 section 5.1): one or more, of
    /// one or more hash functions; the peer's certificate has to match one using the strongest.
    /// </summary>
    public required IReadOnlyList<DtlsFingerprint> Fingerprints { get; init; }

    /// <summary>
    /// The DTLS association's identifier on this side (<c>a=tls-id</c>, RFC 8842 section 4), on the
    /// section carrying the transport; null when not signalled.
    /// </summary>
    public string? TlsId { get; init; }

    /// <summary>The DTLS setup role (<c>a=setup</c>).</summary>
    public SdpSetup Setup { get; init; } = SdpSetup.ActPass;

    /// <summary>Whether RTP and RTCP are multiplexed on one port (<c>a=rtcp-mux</c>); always true here.</summary>
    public bool RtcpMux { get; init; } = true;

    /// <summary>
    /// Whether RTCP may only share the RTP port (<c>a=rtcp-mux-only</c>, RFC 8858); an offer's attribute,
    /// never written in an answer (section 4.3).
    /// </summary>
    public bool RtcpMuxOnly { get; init; }

    /// <summary>
    /// The transport protocol of the <c>m=</c> line; an answer repeats the offer's (RFC 3264 section 6).
    /// </summary>
    public string Protocol { get; init; } = "UDP/TLS/RTP/SAVPF";

    /// <summary>
    /// The <c>m=</c> line's formats as written, for a section whose formats are not RTP payload types
    /// (a data channel's <c>webrtc-datachannel</c>); empty for RTP sections, which list
    /// <see cref="Codecs"/>.
    /// </summary>
    public IReadOnlyList<string> Formats { get; init; } = [];

    /// <summary>
    /// Whether the endpoint receives RTP with encrypted header extensions and CSRCs (<c>a=cryptex</c>, RFC 9335
    /// section 4), at media level or for the whole description at session level.
    /// </summary>
    public bool Cryptex { get; init; }

    /// <summary>
    /// Whether the section is <c>a=bundle-only</c> (RFC 9143 section 6): port 0 without being rejected, to
    /// be used only within the BUNDLE group.
    /// </summary>
    public bool BundleOnly { get; init; }

    /// <summary>
    /// Whether RTCP may be sent as single feedback packets instead of compound reports (<c>a=rtcp-rsize</c>,
    /// RFC 5506); offered always, in effect when both sides list it.
    /// </summary>
    public bool RtcpReducedSize { get; init; } = true;

    /// <summary>
    /// Whether RFC 8888 congestion-control feedback is used: <c>a=rtcp-fb:* ack ccfb</c>, which covers every
    /// payload type in the section, FEC and retransmission included (RFC 8888 section 6). Offered always.
    /// </summary>
    public bool CongestionControlFeedback { get; init; } = true;

    /// <summary>
    /// The media stream this section's track belongs to (<c>a=msid</c>, RFC 8830): every section a sender
    /// sends names the same stream, which RFC 9725 section 4.4.2 asks of WHIP offers. Null for none.
    /// </summary>
    public string? MediaStreamId { get; init; }

    /// <summary>The track's identifier in <see cref="MediaStreamId"/>; null for none.</summary>
    public string? TrackId { get; init; }

    /// <summary>
    /// Whether transport-wide congestion control feedback is offered (<c>a=rtcp-fb:&lt;pt&gt; transport-cc</c>,
    /// with the transport-wide sequence header extension), which browsers send where they do not offer
    /// RFC 8888. Offered always; an answer keeps it only when it does not keep
    /// <see cref="CongestionControlFeedback"/>.
    /// </summary>
    public bool TransportWideFeedback { get; init; } = true;

    /// <summary>
    /// ECN for this media (<c>a=ecn-capable-rtp:</c>, RFC 6679 section 6.1): the initiation methods in
    /// order of preference, and the optional <c>mode</c> and <c>ect</c> parameters; null when not offered.
    /// </summary>
    public SdpEcnCapability? Ecn { get; init; }

    /// <summary>The RTP header extensions mapped for this section (<c>a=extmap</c>, RFC 8285).</summary>
    public IReadOnlyList<SdpExtension> Extensions { get; init; } = [];

    /// <summary>
    /// Whether one- and two-byte extension elements may mix in one packet (<c>a=extmap-allow-mixed</c>,
    /// RFC 8285 section 6).
    /// </summary>
    public bool ExtmapAllowMixed { get; init; }

    /// <summary>The media SSRC of this section, if announced (<c>a=ssrc</c>).</summary>
    public uint? Ssrc { get; init; }

    /// <summary>
    /// The SSRC retransmissions of <see cref="Ssrc"/> travel on (RFC 4588), as
    /// <c>a=ssrc-group:FID media rtx</c> pairs them; null when the section announces none.
    /// </summary>
    public uint? RtxSsrc { get; init; }

    /// <summary>
    /// The SSRC FlexFEC repairs of <see cref="Ssrc"/> travel on (RFC 8627), as
    /// <c>a=ssrc-group:FEC-FR media fec</c> pairs them; null when the section announces none.
    /// </summary>
    public uint? FecSsrc { get; init; }

    /// <summary>
    /// The SSRCs the media source used before a collision moved it to <see cref="Ssrc"/>
    /// (<c>a=ssrc:... previous-ssrc:</c>, RFC 5576 section 6.2); empty for none.
    /// </summary>
    public IReadOnlyList<uint> PreviousSsrcs { get; init; } = [];

    /// <summary>The RTCP canonical name (<c>a=ssrc ... cname:</c>).</summary>
    public string? Cname { get; init; }

    /// <summary>
    /// The ICE candidates carried in the section (<c>a=candidate</c>), as a peer that does not trickle
    /// (WHIP, WHEP, an ICE-lite server) sends them.
    /// </summary>
    public IReadOnlyList<Ice.IceCandidate> Candidates { get; init; } = [];

    /// <summary>
    /// The <c>a=candidate</c> values this stack cannot use, as written: mDNS host names (RFC 8839 section
    /// 5.1), TCP candidates and malformed lines. A peer behind an mDNS name is still reached, as a
    /// peer-reflexive candidate, once its checks arrive.
    /// </summary>
    public IReadOnlyList<string> IgnoredCandidates { get; init; } = [];

    /// <summary>The ICE options the section signals (<c>a=ice-options</c>), at session or media level.</summary>
    public Ice.IceOptions IceOptions { get; init; } = Ice.IceOptions.Trickle;

    /// <summary>Whether the section says no more candidates follow (<c>a=end-of-candidates</c>).</summary>
    public bool EndOfCandidates { get; init; }

    /// <summary>
    /// The section's bandwidth lines: in a remote description, <c>TIAS</c> or <c>AS</c> limit what this
    /// side sends on the section (RFC 8829 section 5.10); in a local one, what this side takes.
    /// </summary>
    public SdpBandwidth Bandwidth { get; init; }
}

/// <summary>
/// One RTP header extension mapping (<c>a=extmap</c>, RFC 8285 section 5): the local identifier the
/// extension travels under, the URI naming it, and an optional direction.
/// </summary>
/// <param name="Id">The identifier: 1 to 14 for the one-byte form, up to 255 for the two-byte form.</param>
/// <param name="Uri">The URI naming the extension.</param>
/// <param name="Direction">The direction, when the mapping states one; otherwise the section's.</param>
/// <param name="Attributes">
/// The extension's own attributes after the URI, as written (<c>vad=off</c> for the audio level); null
/// for none.
/// </param>
public readonly record struct SdpExtension(
    int Id,
    string Uri,
    SdpDirection? Direction = null,
    string? Attributes = null
);

/// <summary>A WebRTC session description: the BUNDLE group plus its media sections (JSEP, RFC 8829).</summary>
public sealed record SdpDescription
{
    /// <summary>The media sections, in m-line order.</summary>
    public required IReadOnlyList<SdpMediaDescription> Media { get; init; }

    /// <summary>The session identifier for the <c>o=</c> line.</summary>
    public long SessionId { get; init; } = SecureRandom.SessionId();

    /// <summary>
    /// The <c>o=</c> session version: the same session id across a connection's descriptions, the version
    /// one higher each time the description changes (RFC 3264 section 8, RFC 8866 section 5.2).
    /// </summary>
    public long SessionVersion { get; init; } = 1;

    /// <summary>
    /// The mids of the BUNDLE group (<c>a=group:BUNDLE</c>, RFC 9143), offerer-tagged first; null when the
    /// description has none. An answer has one only when the offer did (section 7.3).
    /// </summary>
    public IReadOnlyList<string>? BundleGroup { get; init; }

    /// <summary>Whether the peer runs ICE-lite (<c>a=ice-lite</c>), answering checks without making its own.</summary>
    public bool IceLite { get; init; }

    /// <summary>
    /// The session-level bandwidth lines: <c>CT</c> for all sections together, and <c>RS</c> and <c>RR</c>
    /// for the RTP session's RTCP. A session-level <c>AS</c> is not read (RFC 8829 section 5.10).
    /// </summary>
    public SdpBandwidth Bandwidth { get; init; }
}

/// <summary>
/// The bandwidth lines of a description or a section (<c>b=</c>, RFC 8866 section 5.8), all in bits per
/// second: <c>AS</c> and <c>CT</c> are written in kilobits per second, <c>TIAS</c> (RFC 3890) and <c>RS</c>
/// and <c>RR</c> (RFC 3556) in bits per second. A section carries <c>AS</c>, <c>TIAS</c>, <c>RS</c> and
/// <c>RR</c>; a description <c>CT</c>, <c>RS</c> and <c>RR</c>, as JSEP (RFC 8829 section 5.10) applies them.
/// Null where the line is absent.
/// </summary>
/// <param name="ApplicationSpecific">
/// <c>b=AS</c>: the most the section's media may take, transport overhead included.
/// </param>
/// <param name="TransportIndependent">
/// <c>b=TIAS</c>: the most the section's media may take without transport overhead.
/// </param>
/// <param name="RtcpSenders">
/// <c>b=RS</c>: the RTCP bandwidth for active senders (RFC 3556 section 2).
/// </param>
/// <param name="RtcpReceivers">
/// <c>b=RR</c>: the RTCP bandwidth for the other participants (RFC 3556 section 2).
/// </param>
/// <param name="ConferenceTotal">
/// <c>b=CT</c>, session level: the most all sections together may take.
/// </param>
public readonly record struct SdpBandwidth(
    long? ApplicationSpecific = null,
    long? TransportIndependent = null,
    long? RtcpSenders = null,
    long? RtcpReceivers = null,
    long? ConferenceTotal = null
)
{
    /// <summary>Whether no bandwidth line is present.</summary>
    public bool IsEmpty =>
        ApplicationSpecific is null
        && TransportIndependent is null
        && RtcpSenders is null
        && RtcpReceivers is null
        && ConferenceTotal is null;

    /// <summary>
    /// The most RTP the section's sender may send, in bits per second (RFC 8829 section 5.10): the
    /// <c>TIAS</c> value, or one derived from <c>AS</c> as 95% of it (5% is RTCP's) less 50 packets a second
    /// of 40-byte headers. Null when the section sets neither.
    /// </summary>
    public long? MaximumRtpBitsPerSecond =>
        TransportIndependent
        ?? (
            ApplicationSpecific is { } bits ? Math.Max(0, (bits / 100 * 95) - (50 * 40 * 8)) : null
        );
}

/// <summary>An <c>a=ecn-capable-rtp:</c> attribute (RFC 6679 section 6.1).</summary>
/// <param name="Methods">The ECN initiation methods (<c>rtp</c>, <c>ice</c>, <c>leap</c>), most preferred first.</param>
/// <param name="Mode">
/// <c>setread</c>, <c>setonly</c> or <c>readonly</c>: what the endpoint can do with the ECN field;
/// <c>setread</c> when absent.
/// </param>
/// <param name="Ect">The ECT codepoint the endpoint prefers to receive: <c>0</c>, <c>1</c> or <c>random</c>; 0 when absent.</param>
public sealed record SdpEcnCapability(
    System.Collections.Immutable.ImmutableArray<string> Methods,
    string? Mode,
    string? Ect
)
{
    /// <summary>Whether the endpoint can mark the datagrams it sends.</summary>
    public bool CanSet => Mode is null or "setread" or "setonly";

    /// <summary>Whether the endpoint can read the marks of the datagrams it receives.</summary>
    public bool CanRead => Mode is null or "setread" or "readonly";
}
