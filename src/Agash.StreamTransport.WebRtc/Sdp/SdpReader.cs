using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using Agash.StreamTransport.WebRtc.Ice;
using Dtls.Core;

namespace Agash.StreamTransport.WebRtc.Sdp;

/// <summary>
/// Parses WebRTC SDP into a <see cref="SdpDescription"/>. Tolerant and line-oriented: session-level ICE
/// credentials, fingerprint, and setup are inherited by media sections that omit their own (as a browser
/// may place them at either level).
/// </summary>
public static class SdpReader
{
    /// <summary>Parses SDP text. Returns <see langword="false"/> if no usable media section is found.</summary>
    public static bool TryParse(string sdp, out SdpDescription description)
    {
        description = null!;
        if (string.IsNullOrWhiteSpace(sdp))
        {
            return false;
        }

        string sessionUfrag = "",
            sessionPwd = "";
        ImmutableArray<DtlsFingerprint>.Builder sessionFingerprints =
            ImmutableArray.CreateBuilder<DtlsFingerprint>();
        SdpSetup? sessionSetup = SdpSetup.ActPass;
        bool iceLite = false;
        List<IceCandidate> sessionCandidates = [];
        List<string> sessionIgnored = [];
        IceOptions sessionIceOptions = IceOptions.None;
        ImmutableArray<SdpExtension>.Builder sessionExtensions =
            ImmutableArray.CreateBuilder<SdpExtension>();
        bool sessionAllowMixed = false;

        var media = new List<MediaBuilder>();
        MediaBuilder? current = null;
        bool skipping = false;
        long sessionId = 0;
        long sessionVersion = 0;
        ImmutableArray<string>? bundleGroup = null;
        SdpBandwidth sessionBandwidth = default;
        bool sessionCryptex = false;

        foreach (string raw in sdp.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length < 2)
            {
                continue;
            }

            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                current = MediaBuilder.FromMLine(line);
                if (current is not null)
                {
                    media.Add(current);
                }

                // A section this reader cannot read keeps its attributes: they must not reach the session.
                skipping = current is null;
                continue;
            }

            if (skipping)
            {
                continue;
            }

            if (current is null && TryValue(line, "o=", out string origin))
            {
                string[] fields = origin.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 3)
                {
                    _ = long.TryParse(
                        fields[1],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out sessionId
                    );
                    _ = long.TryParse(
                        fields[2],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out sessionVersion
                    );
                }

                continue;
            }

            if (TryValue(line, "b=", out string bandwidthLine))
            {
                // RFC 8829 section 5.10: a malformed or negative value fails the description; an unknown
                // type, and AS at session level, is ignored.
                if (current is null)
                {
                    if (!TryApplyBandwidth(bandwidthLine, ref sessionBandwidth, session: true))
                    {
                        return false;
                    }
                }
                else if (!current.ApplyBandwidth(bandwidthLine))
                {
                    return false;
                }

                continue;
            }

            if (current is null && TryValue(line, "a=group:BUNDLE", out string group))
            {
                bundleGroup = ImmutableCollectionsMarshal.AsImmutableArray(
                    group.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                );
                continue;
            }

            if (line == "a=ice-lite")
            {
                iceLite = true;
                continue;
            }

            // RFC 9335 section 4: a property attribute, at session or media level.
            if (line == "a=cryptex")
            {
                if (current is null)
                {
                    sessionCryptex = true;
                }
                else
                {
                    current.Cryptex = true;
                }

                continue;
            }

            if (TryValue(line, "a=ice-options:", out string iceOptions))
            {
                if (current is null)
                {
                    sessionIceOptions = IceOptionsText.Parse(iceOptions);
                }
                else
                {
                    current.IceOptions = IceOptionsText.Parse(iceOptions);
                }

                continue;
            }

            if (TryValue(line, "a=candidate:", out _))
            {
                if (IceCandidate.TryParse(line, out IceCandidate parsed))
                {
                    (current?.Candidates ?? sessionCandidates).Add(parsed);
                }
                else
                {
                    (current?.IgnoredCandidates ?? sessionIgnored).Add(line[2..]);
                }

                continue;
            }

            if (current is null && TryValue(line, "a=extmap:", out string extmap))
            {
                if (ParseExtmap(extmap) is { } extension)
                {
                    sessionExtensions.Add(extension);
                }

                continue;
            }

            if (current is null && line == "a=extmap-allow-mixed")
            {
                sessionAllowMixed = true;
                continue;
            }

            if (current is null)
            {
                ApplySessionAttribute(
                    line,
                    ref sessionUfrag,
                    ref sessionPwd,
                    sessionFingerprints,
                    ref sessionSetup
                );
            }
            else
            {
                current.Apply(line);
            }
        }

        if (media.Count == 0)
        {
            return false;
        }

        ImmutableArray<DtlsFingerprint> sharedFingerprints = sessionFingerprints.DrainToImmutable();
        ImmutableArray<SdpExtension> sharedExtensions = sessionExtensions.DrainToImmutable();
        ImmutableArray<SdpMediaDescription>.Builder result =
            ImmutableArray.CreateBuilder<SdpMediaDescription>(media.Count);

        // RFC 9143 section 7.1.3: in an answer or a subsequent offer the BUNDLE attributes (IDENTICAL and
        // TRANSPORT: ICE, DTLS, rtcp-mux) appear only in the tagged section and apply to every section
        // bundled with it, so a bundled section without its own takes the tagged section's.
        MediaBuilder? tagged = TaggedSection(media, bundleGroup);
        for (int index = 0; index < media.Count; index++)
        {
            MediaBuilder m = media[index];
            MediaBuilder? bundle =
                tagged is not null
                && !ReferenceEquals(m, tagged)
                && bundleGroup!.Value.Contains(m.Mid ?? "")
                    ? tagged
                    : null;
            ImmutableArray<DtlsFingerprint> fingerprints =
                m.Fingerprints.Count > 0 ? m.Fingerprints.ToImmutable()
                : bundle is { Fingerprints.Count: > 0 } ? bundle.Fingerprints.ToImmutable()
                : sharedFingerprints;
            SdpSetup? setup =
                m.SetupSeen ? m.Setup
                : bundle is { SetupSeen: true } ? bundle.Setup
                : sessionSetup;
            bool rejected = m.PortZero && !m.BundleOnly;

            // JSEP: no usable fingerprint is a negotiation failure. RFC 8842 section 5.1: DTLS never uses
            // holdconn, and a setup value outside RFC 4145's is not SDP this reader can act on. RFC 8842
            // section 4: a tls-id outside its syntax is malformed. A rejected section negotiates nothing,
            // so it needs neither.
            if (!rejected && (fingerprints.Length == 0 || setup is null) || m.InvalidTlsId)
            {
                return false;
            }

            result.Add(
                new SdpMediaDescription
                {
                    Kind = m.Kind,
                    Mid = m.Mid ?? result.Count.ToString(CultureInfo.InvariantCulture),
                    Direction = m.Direction,
                    // RFC 9143 section 6: port 0 with bundle-only is a bundled section, not a rejected one.
                    Rejected = rejected,
                    BundleOnly = m.BundleOnly,
                    Protocol = m.Protocol,
                    Formats = m.Formats,
                    RtcpMuxOnly = m.RtcpMuxOnly || bundle?.RtcpMuxOnly == true,
                    Cryptex = m.Cryptex || sessionCryptex,
                    Codecs = m.BuildCodecs(),
                    IceUfrag = m.IceUfrag ?? bundle?.IceUfrag ?? sessionUfrag,
                    IcePwd = m.IcePwd ?? bundle?.IcePwd ?? sessionPwd,
                    Fingerprints = fingerprints,
                    TlsId = m.TlsId ?? bundle?.TlsId,
                    Setup = setup ?? SdpSetup.ActPass,
                    RtcpMux = m.RtcpMux || bundle?.RtcpMux == true,
                    RtcpReducedSize = m.RtcpReducedSize || bundle?.RtcpReducedSize == true,
                    CongestionControlFeedback = m.CongestionControlFeedback,
                    TransportWideFeedback = m.TransportWideFeedback,
                    Ecn = m.Ecn,
                    // RFC 8285 section 5: mappings are all at session level or all at media level.
                    Extensions =
                        m.Extensions.Count > 0 ? m.Extensions.DrainToImmutable() : sharedExtensions,
                    ExtmapAllowMixed = m.ExtmapAllowMixed || sessionAllowMixed,
                    Ssrc = m.Ssrc,
                    RtxSsrc = m.RtxSsrc,
                    FecSsrc = m.FecSsrc,
                    PreviousSsrcs = m.PreviousSsrcs.DrainToImmutable(),
                    MediaStreamId = m.MediaStreamId,
                    TrackId = m.TrackId,
                    Cname = m.Cname,
                    Candidates = [.. sessionCandidates, .. m.Candidates],
                    IgnoredCandidates = [.. sessionIgnored, .. m.IgnoredCandidates],
                    IceOptions = m.IceOptions ?? bundle?.IceOptions ?? sessionIceOptions,
                    EndOfCandidates = m.EndOfCandidates,
                    Bandwidth = m.Bandwidth,
                }
            );
        }

        description = new SdpDescription
        {
            Media = result.DrainToImmutable(),
            IceLite = iceLite,
            BundleGroup = bundleGroup,
            SessionId = sessionId,
            SessionVersion = sessionVersion,
            Bandwidth = sessionBandwidth,
        };
        return true;
    }

    // The section a BUNDLE group's attributes come from: the first of its list with a port, so neither
    // rejected nor bundle-only (RFC 9143 sections 7.2.1 and 7.3.1).
    private static MediaBuilder? TaggedSection(
        List<MediaBuilder> media,
        ImmutableArray<string>? group
    )
    {
        if (group is not { } mids)
        {
            return null;
        }

        foreach (string mid in mids)
        {
            foreach (MediaBuilder m in media)
            {
                if (m.Mid == mid && !m.PortZero)
                {
                    return m;
                }
            }
        }

        return null;
    }

    // One "b=<type>:<value>" line (RFC 8866 section 5.8) into the bandwidth it belongs to, in bits per
    // second; false when the value is not a non-negative integer.
    private static bool TryApplyBandwidth(string line, ref SdpBandwidth bandwidth, bool session)
    {
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        if (
            colon <= 0
            || !long.TryParse(
                line.AsSpan(colon + 1).Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long value
            )
        )
        {
            return false;
        }

        // AS and CT count kilobits; saturate rather than overflow.
        long kilobits = value > long.MaxValue / 1000 ? long.MaxValue : value * 1000;
        bandwidth = line[..colon] switch
        {
            "AS" when !session => bandwidth with { ApplicationSpecific = kilobits },
            "CT" when session => bandwidth with { ConferenceTotal = kilobits },
            "TIAS" when !session => bandwidth with { TransportIndependent = value },
            "RS" => bandwidth with { RtcpSenders = value },
            "RR" => bandwidth with { RtcpReceivers = value },
            _ => bandwidth,
        };
        return true;
    }

    private static void ApplySessionAttribute(
        string line,
        ref string ufrag,
        ref string pwd,
        ImmutableArray<DtlsFingerprint>.Builder fingerprints,
        ref SdpSetup? setup
    )
    {
        if (TryValue(line, "a=ice-ufrag:", out string u))
        {
            ufrag = u;
        }
        else if (TryValue(line, "a=ice-pwd:", out string p))
        {
            pwd = p;
        }
        else if (
            TryValue(line, "a=fingerprint:", out string f) && ParseFingerprint(f) is { } parsed
        )
        {
            fingerprints.Add(parsed);
        }
        else if (TryValue(line, "a=setup:", out string s))
        {
            setup = ParseSetup(s);
        }
    }

    // "<id>[/<direction>] <uri> [<attributes>]" (RFC 8285 section 8); malformed mappings are ignored.
    internal static SdpExtension? ParseExtmap(string value)
    {
        string[] parts = value.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        string[] idAndDirection = parts[0].Split('/');
        if (
            !int.TryParse(
                idAndDirection[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int id
            )
        )
        {
            return null;
        }

        SdpDirection? direction =
            idAndDirection.Length > 1
                ? idAndDirection[1] switch
                {
                    "sendonly" => SdpDirection.SendOnly,
                    "recvonly" => SdpDirection.RecvOnly,
                    "inactive" => SdpDirection.Inactive,
                    _ => SdpDirection.SendRecv,
                }
                : null;
        return new SdpExtension(id, parts[1], direction, parts.Length > 2 ? parts[2].Trim() : null);
    }

    internal static bool TryValue(string line, string prefix, out string value)
    {
        if (line.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = line[prefix.Length..].Trim();
            return true;
        }

        value = "";
        return false;
    }

    // A fingerprint with a hash function this reader cannot verify (MD5, MD2, an unknown token) is
    // left out; one of the others has to match (RFC 8122 section 5).
    internal static DtlsFingerprint? ParseFingerprint(string value) =>
        DtlsFingerprint.TryParse(value, out DtlsFingerprint fingerprint) ? fingerprint : null;

    // RFC 4145's values less holdconn, which RFC 8842 section 5.1 forbids for DTLS; null for anything else.
    private static SdpSetup? ParseSetup(string value) =>
        value switch
        {
            "actpass" => SdpSetup.ActPass,
            "active" => SdpSetup.Active,
            "passive" => SdpSetup.Passive,
            _ => null,
        };

    // RFC 8842 section 4: tls-id-value = 20*255(ALPHA / DIGIT / "+" / "/" / "-" / "_").
    internal static bool IsTlsId(string value) =>
        value.Length is >= 20 and <= 255
        && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '-' or '_');

    private sealed class MediaBuilder
    {
        private readonly List<int> _payloadOrder = [];
        private readonly Dictionary<int, (string Name, int Clock, int? Channels)> _rtpmap = [];
        private readonly Dictionary<int, string> _fmtp = [];
        private readonly Dictionary<int, List<string>> _feedback = [];

        public SdpMediaKind Kind { get; private init; }
        public string? Mid { get; private set; }

        public string? MediaStreamId { get; private set; }

        // RFC 8830 section 2 and RFC 8866 section 9: 1*64 token-char.
        private static bool IsMsidId(string id) =>
            id.Length is > 0 and <= 64 && !id.AsSpan().ContainsAnyExcept(TokenChars);

        private static readonly System.Buffers.SearchValues<char> TokenChars =
            System.Buffers.SearchValues.Create(
                "!#$%&'*+-.0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ^_`abcdefghijklmnopqrstuvwxyz{|}~"
            );

        public string? TrackId { get; private set; }
        public SdpDirection Direction { get; private set; } = SdpDirection.SendRecv;
        public string? IceUfrag { get; private set; }
        public string? IcePwd { get; private set; }
        public ImmutableArray<DtlsFingerprint>.Builder Fingerprints { get; } =
            ImmutableArray.CreateBuilder<DtlsFingerprint>();
        public SdpSetup? Setup { get; private set; }
        public bool SetupSeen { get; private set; }
        public string? TlsId { get; private set; }
        public bool InvalidTlsId { get; private set; }
        public SdpBandwidth Bandwidth { get; private set; }

        public bool ApplyBandwidth(string line)
        {
            SdpBandwidth bandwidth = Bandwidth;
            bool read = TryApplyBandwidth(line, ref bandwidth, session: false);
            Bandwidth = bandwidth;
            return read;
        }

        public bool RtcpMux { get; private set; }

        public bool RtcpReducedSize { get; private set; }

        public bool CongestionControlFeedback { get; private set; }

        public bool TransportWideFeedback { get; private set; }

        public SdpEcnCapability? Ecn { get; private set; }

        public ImmutableArray<SdpExtension>.Builder Extensions { get; } =
            ImmutableArray.CreateBuilder<SdpExtension>();

        public bool ExtmapAllowMixed { get; private set; }

        // "rtp,ice mode=setread; ect=1": the initiation methods, then parameters (RFC 6679 section 6.1);
        // unknown methods and parameters are ignored.
        private static SdpEcnCapability? ParseEcn(string value)
        {
            string trimmed = value.Trim();
            int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
            string methods = space < 0 ? trimmed : trimmed[..space];
            string? mode = null;
            string? ect = null;
            if (space > 0)
            {
                foreach (
                    string parameter in trimmed[(space + 1)..]
                        .Split(
                            ';',
                            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                        )
                )
                {
                    if (parameter.StartsWith("mode=", StringComparison.Ordinal))
                    {
                        mode = parameter[5..];
                    }
                    else if (parameter.StartsWith("ect=", StringComparison.Ordinal))
                    {
                        ect = parameter[4..];
                    }
                }
            }

            System.Collections.Immutable.ImmutableArray<string> list =
            [
                .. methods.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                ),
            ];
            return list.IsEmpty ? null : new SdpEcnCapability(list, mode, ect);
        }

        private readonly List<string> _wildcardFeedback = [];
        public uint? Ssrc { get; private set; }
        public string? Cname { get; private set; }
        public uint? RtxSsrc { get; private set; }

        public uint? FecSsrc { get; private set; }

        public ImmutableArray<uint>.Builder PreviousSsrcs { get; } =
            ImmutableArray.CreateBuilder<uint>();
        public bool PortZero { get; init; }
        public bool BundleOnly { get; private set; }
        public bool RtcpMuxOnly { get; private set; }

        public bool Cryptex { get; set; }
        public string Protocol { get; init; } = "";
        public ImmutableArray<string> Formats { get; init; } = [];
        public List<IceCandidate> Candidates { get; } = [];

        public List<string> IgnoredCandidates { get; } = [];

        public IceOptions? IceOptions { get; set; }
        public bool EndOfCandidates { get; private set; }

        public static MediaBuilder? FromMLine(string line)
        {
            // m=<kind> <port> <proto> <pt> <pt> ...
            string[] parts = line[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                return null;
            }

            SdpMediaKind kind = parts[0] switch
            {
                "audio" => SdpMediaKind.Audio,
                "video" => SdpMediaKind.Video,
                _ => SdpMediaKind.Application,
            };

            var builder = new MediaBuilder
            {
                Kind = kind,
                PortZero = parts[1] == "0",
                Protocol = parts[2],
                Formats =
                    parts.Length > 3
                        ? ImmutableCollectionsMarshal.AsImmutableArray(parts[3..])
                        : [],
            };
            for (int i = 3; i < parts.Length; i++)
            {
                if (
                    int.TryParse(
                        parts[i],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int pt
                    )
                )
                {
                    builder._payloadOrder.Add(pt);
                }
            }

            return builder;
        }

        public void Apply(string line)
        {
            if (TryValue(line, "a=mid:", out string mid))
            {
                Mid = mid;
            }
            else if (TryValue(line, "a=msid:", out string msid))
            {
                // RFC 8830 section 2: msid-id [SP msid-appdata], each 1*64 token-char; an id that is not
                // leaves the section without one.
                int space = msid.IndexOf(' ', StringComparison.Ordinal);
                string stream = space < 0 ? msid : msid[..space];
                string? track = space < 0 ? null : msid[(space + 1)..].Trim();
                if (IsMsidId(stream) && (track is null || IsMsidId(track)))
                {
                    MediaStreamId = stream;
                    TrackId = track;
                }
            }
            else if (TryValue(line, "a=ice-ufrag:", out string u))
            {
                IceUfrag = u;
            }
            else if (TryValue(line, "a=ice-pwd:", out string p))
            {
                IcePwd = p;
            }
            else if (TryValue(line, "a=fingerprint:", out string f))
            {
                if (ParseFingerprint(f) is { } fingerprint)
                {
                    Fingerprints.Add(fingerprint);
                }
            }
            else if (TryValue(line, "a=setup:", out string s))
            {
                Setup = ParseSetup(s);
                SetupSeen = true;
            }
            else if (TryValue(line, "a=tls-id:", out string tlsId))
            {
                TlsId = IsTlsId(tlsId) ? tlsId : null;
                InvalidTlsId |= TlsId is null;
            }
            else if (line == "a=rtcp-mux")
            {
                RtcpMux = true;
            }
            else if (line == "a=rtcp-mux-only")
            {
                RtcpMuxOnly = true;
            }
            else if (line == "a=bundle-only")
            {
                BundleOnly = true;
            }
            else if (line == "a=rtcp-rsize")
            {
                RtcpReducedSize = true;
            }
            else if (line == "a=end-of-candidates")
            {
                EndOfCandidates = true;
            }
            else if (line == "a=sendrecv")
            {
                Direction = SdpDirection.SendRecv;
            }
            else if (line == "a=sendonly")
            {
                Direction = SdpDirection.SendOnly;
            }
            else if (line == "a=recvonly")
            {
                Direction = SdpDirection.RecvOnly;
            }
            else if (line == "a=inactive")
            {
                Direction = SdpDirection.Inactive;
            }
            else if (TryValue(line, "a=rtpmap:", out string rtpmap))
            {
                ParseRtpmap(rtpmap);
            }
            else if (TryValue(line, "a=fmtp:", out string fmtp))
            {
                ParseFmtp(fmtp);
            }
            else if (TryValue(line, "a=rtcp-fb:", out string fb))
            {
                ParseFeedback(fb);
            }
            else if (TryValue(line, "a=ecn-capable-rtp:", out string ecn))
            {
                Ecn = ParseEcn(ecn);
            }
            else if (TryValue(line, "a=extmap:", out string extmap))
            {
                if (ParseExtmap(extmap) is { } extension)
                {
                    Extensions.Add(extension);
                }
            }
            else if (line == "a=extmap-allow-mixed")
            {
                ExtmapAllowMixed = true;
            }
            else if (TryValue(line, "a=ssrc-group:FID ", out string fid))
            {
                ParseFid(fid);
            }
            else if (TryValue(line, "a=ssrc-group:FEC-FR ", out string fecFr))
            {
                ParseFecFr(fecFr);
            }
            else if (TryValue(line, "a=ssrc:", out string ssrc))
            {
                ParseSsrc(ssrc);
            }
        }

        public ImmutableArray<SdpCodec> BuildCodecs()
        {
            ImmutableArray<SdpCodec>.Builder codecs = ImmutableArray.CreateBuilder<SdpCodec>(
                _payloadOrder.Count
            );
            foreach (int pt in _payloadOrder)
            {
                if (
                    !_rtpmap.TryGetValue(pt, out (string Name, int Clock, int? Channels) map)
                    && !StaticPayloadTypes.TryGetValue(pt, out map)
                )
                {
                    continue;
                }

                codecs.Add(
                    new SdpCodec(
                        pt,
                        map.Name,
                        map.Clock,
                        map.Channels,
                        _fmtp.GetValueOrDefault(pt),
                        [
                            .. _feedback.TryGetValue(pt, out List<string>? fb) ? fb : [],
                            .. _wildcardFeedback,
                        ]
                    )
                );
            }

            return codecs.DrainToImmutable();
        }

        // RFC 3551 section 6: payload types a description may list without an a=rtpmap.
        private static readonly Dictionary<
            int,
            (string Name, int Clock, int? Channels)
        > StaticPayloadTypes = new()
        {
            [0] = ("PCMU", 8000, 1),
            [3] = ("GSM", 8000, 1),
            [4] = ("G723", 8000, 1),
            [8] = ("PCMA", 8000, 1),
            [9] = ("G722", 8000, 1),
            [13] = ("CN", 8000, 1),
            [18] = ("G729", 8000, 1),
        };

        private void ParseRtpmap(string value)
        {
            // <pt> <name>/<clock>[/<channels>]
            int space = value.IndexOf(' ', StringComparison.Ordinal);
            if (space <= 0 || !int.TryParse(value[..space], out int pt))
            {
                return;
            }

            string[] codec = value[(space + 1)..].Split('/');
            if (codec.Length < 2 || !int.TryParse(codec[1], out int clock))
            {
                return;
            }

            int? channels = codec.Length >= 3 && int.TryParse(codec[2], out int ch) ? ch : null;
            _rtpmap[pt] = (codec[0], clock, channels);
        }

        private void ParseFmtp(string value)
        {
            int space = value.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0 && int.TryParse(value[..space], out int pt))
            {
                _fmtp[pt] = value[(space + 1)..];
            }
        }

        // "a=rtcp-fb:<pt> <feedback>"; "*" applies to every payload type (RFC 4585 section 4.2).
        private void ParseFeedback(string value)
        {
            int space = value.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0 && value[(space + 1)..] == "ack ccfb")
            {
                // RFC 8888 asks for the wildcard; a peer that lists it per payload type means the same.
                CongestionControlFeedback = true;
            }
            else if (space > 0 && value[(space + 1)..] == "transport-cc")
            {
                // Listed per payload type by browsers; it is one feedback stream for the whole transport.
                TransportWideFeedback = true;
            }
            else if (space == 1 && value[0] == '*')
            {
                _wildcardFeedback.Add(value[(space + 1)..]);
            }
            else if (space > 0 && int.TryParse(value[..space], out int pt))
            {
                (_feedback.TryGetValue(pt, out List<string>? list) ? list : _feedback[pt] = []).Add(
                    value[(space + 1)..]
                );
            }
        }

        // a=ssrc lines name the media SSRC first; a later one (the RTX SSRC, a FEC SSRC) does not
        // replace it. An FID group says which is which.
        private void ParseSsrc(string value)
        {
            string[] parts = value.Split(' ', 2);
            if (
                uint.TryParse(
                    parts[0],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out uint ssrc
                )
            )
            {
                Ssrc ??= ssrc;
                if (parts.Length == 2 && TryValue(parts[1], "cname:", out string cname))
                {
                    Cname ??= cname;
                }
                else if (
                    parts.Length == 2
                    && TryValue(parts[1], "previous-ssrc:", out string previous)
                    && ssrc == Ssrc
                )
                {
                    // RFC 5576 section 6.2: one or more SSRCs the source used before.
                    foreach (
                        string each in previous.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    )
                    {
                        if (
                            uint.TryParse(
                                each,
                                NumberStyles.None,
                                CultureInfo.InvariantCulture,
                                out uint old
                            )
                        )
                        {
                            PreviousSsrcs.Add(old);
                        }
                    }
                }
            }
        }

        // a=ssrc-group:FEC-FR <media> <fec> (RFC 5956, RFC 8627).
        private void ParseFecFr(string value)
        {
            string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (
                parts.Length == 2
                && uint.TryParse(parts[0], out uint media)
                && uint.TryParse(parts[1], out uint fec)
            )
            {
                Ssrc = media;
                FecSsrc = fec;
            }
        }

        // a=ssrc-group:FID <media> <rtx> (RFC 5576, RFC 4588).
        private void ParseFid(string value)
        {
            string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (
                parts.Length == 2
                && uint.TryParse(parts[0], out uint media)
                && uint.TryParse(parts[1], out uint rtx)
            )
            {
                Ssrc = media;
                RtxSsrc = rtx;
            }
        }
    }
}
