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

/// <summary>The DTLS role advertised by <c>a=setup</c> (RFC 8842).</summary>
public enum SdpSetup
{
    /// <summary>actpass — offer the choice (offers).</summary>
    ActPass,

    /// <summary>active — be the DTLS client.</summary>
    Active,

    /// <summary>passive — be the DTLS server.</summary>
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
    IReadOnlyList<string> RtcpFeedback);

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

    /// <summary>The offered/answered codecs in preference order.</summary>
    public required IReadOnlyList<SdpCodec> Codecs { get; init; }

    /// <summary>The ICE username fragment (<c>a=ice-ufrag</c>).</summary>
    public required string IceUfrag { get; init; }

    /// <summary>The ICE password (<c>a=ice-pwd</c>).</summary>
    public required string IcePwd { get; init; }

    /// <summary>The DTLS certificate fingerprint (<c>a=fingerprint</c>).</summary>
    public required DtlsFingerprint Fingerprint { get; init; }

    /// <summary>The DTLS setup role (<c>a=setup</c>).</summary>
    public SdpSetup Setup { get; init; } = SdpSetup.ActPass;

    /// <summary>Whether RTP and RTCP are multiplexed on one port (<c>a=rtcp-mux</c>); always true here.</summary>
    public bool RtcpMux { get; init; } = true;

    /// <summary>The local SSRC for this section, if announced (<c>a=ssrc</c>).</summary>
    public uint? Ssrc { get; init; }

    /// <summary>The RTCP canonical name (<c>a=ssrc … cname:</c>).</summary>
    public string? Cname { get; init; }
}

/// <summary>A WebRTC session description: the BUNDLE group plus its media sections (JSEP, RFC 8829).</summary>
public sealed record SdpDescription
{
    /// <summary>The media sections, in m-line order.</summary>
    public required IReadOnlyList<SdpMediaDescription> Media { get; init; }

    /// <summary>The session identifier for the <c>o=</c> line.</summary>
    public long SessionId { get; init; } = Random.Shared.NextInt64(1, long.MaxValue);
}
