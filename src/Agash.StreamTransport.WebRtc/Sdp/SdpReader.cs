using System.Globalization;

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

        string sessionUfrag = "", sessionPwd = "";
        DtlsFingerprint? sessionFingerprint = null;
        SdpSetup sessionSetup = SdpSetup.ActPass;

        var media = new List<MediaBuilder>();
        MediaBuilder? current = null;

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

                continue;
            }

            if (current is null)
            {
                ApplySessionAttribute(line, ref sessionUfrag, ref sessionPwd, ref sessionFingerprint, ref sessionSetup);
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

        var result = new List<SdpMediaDescription>(media.Count);
        foreach (MediaBuilder m in media)
        {
            DtlsFingerprint? fingerprint = m.Fingerprint ?? sessionFingerprint;
            if (fingerprint is null)
            {
                return false; // JSEP: absence of a fingerprint is a negotiation failure.
            }

            result.Add(new SdpMediaDescription
            {
                Kind = m.Kind,
                Mid = m.Mid ?? result.Count.ToString(CultureInfo.InvariantCulture),
                Direction = m.Direction,
                Codecs = m.BuildCodecs(),
                IceUfrag = m.IceUfrag ?? sessionUfrag,
                IcePwd = m.IcePwd ?? sessionPwd,
                Fingerprint = fingerprint.Value,
                Setup = m.Setup ?? sessionSetup,
                RtcpMux = m.RtcpMux,
                Ssrc = m.Ssrc,
                Cname = m.Cname,
            });
        }

        description = new SdpDescription { Media = result };
        return true;
    }

    private static void ApplySessionAttribute(
        string line, ref string ufrag, ref string pwd, ref DtlsFingerprint? fingerprint, ref SdpSetup setup)
    {
        if (TryValue(line, "a=ice-ufrag:", out string u))
        {
            ufrag = u;
        }
        else if (TryValue(line, "a=ice-pwd:", out string p))
        {
            pwd = p;
        }
        else if (TryValue(line, "a=fingerprint:", out string f) && ParseFingerprint(f) is { } parsed)
        {
            fingerprint = parsed;
        }
        else if (TryValue(line, "a=setup:", out string s))
        {
            setup = s switch { "active" => SdpSetup.Active, "passive" => SdpSetup.Passive, _ => SdpSetup.ActPass };
        }
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

    internal static DtlsFingerprint? ParseFingerprint(string value)
    {
        int space = value.IndexOf(' ', StringComparison.Ordinal);
        if (space <= 0)
        {
            return null;
        }

        string algorithm = value[..space];
        string hex = value[(space + 1)..].Replace(":", "", StringComparison.Ordinal).Trim();
        try
        {
            return new DtlsFingerprint(algorithm, Convert.FromHexString(hex));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed class MediaBuilder
    {
        private readonly List<int> _payloadOrder = [];
        private readonly Dictionary<int, (string Name, int Clock, int? Channels)> _rtpmap = [];
        private readonly Dictionary<int, string> _fmtp = [];
        private readonly Dictionary<int, List<string>> _feedback = [];

        public SdpMediaKind Kind { get; private init; }
        public string? Mid { get; private set; }
        public SdpDirection Direction { get; private set; } = SdpDirection.SendRecv;
        public string? IceUfrag { get; private set; }
        public string? IcePwd { get; private set; }
        public DtlsFingerprint? Fingerprint { get; private set; }
        public SdpSetup? Setup { get; private set; }
        public bool RtcpMux { get; private set; }
        public uint? Ssrc { get; private set; }
        public string? Cname { get; private set; }

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

            var builder = new MediaBuilder { Kind = kind };
            for (int i = 3; i < parts.Length; i++)
            {
                if (int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pt))
                {
                    builder._payloadOrder.Add(pt);
                }
            }

            return builder;
        }

        public void Apply(string line)
        {
            if (TryValue(line, "a=mid:", out string mid)) { Mid = mid; }
            else if (TryValue(line, "a=ice-ufrag:", out string u)) { IceUfrag = u; }
            else if (TryValue(line, "a=ice-pwd:", out string p)) { IcePwd = p; }
            else if (TryValue(line, "a=fingerprint:", out string f)) { Fingerprint = ParseFingerprint(f); }
            else if (TryValue(line, "a=setup:", out string s)) { Setup = s switch { "active" => SdpSetup.Active, "passive" => SdpSetup.Passive, _ => SdpSetup.ActPass }; }
            else if (line == "a=rtcp-mux") { RtcpMux = true; }
            else if (line == "a=sendrecv") { Direction = SdpDirection.SendRecv; }
            else if (line == "a=sendonly") { Direction = SdpDirection.SendOnly; }
            else if (line == "a=recvonly") { Direction = SdpDirection.RecvOnly; }
            else if (line == "a=inactive") { Direction = SdpDirection.Inactive; }
            else if (TryValue(line, "a=rtpmap:", out string rtpmap)) { ParseRtpmap(rtpmap); }
            else if (TryValue(line, "a=fmtp:", out string fmtp)) { ParseFmtp(fmtp); }
            else if (TryValue(line, "a=rtcp-fb:", out string fb)) { ParseFeedback(fb); }
            else if (TryValue(line, "a=ssrc:", out string ssrc)) { ParseSsrc(ssrc); }
        }

        public IReadOnlyList<SdpCodec> BuildCodecs()
        {
            var codecs = new List<SdpCodec>(_payloadOrder.Count);
            foreach (int pt in _payloadOrder)
            {
                if (!_rtpmap.TryGetValue(pt, out (string Name, int Clock, int? Channels) map))
                {
                    continue;
                }

                codecs.Add(new SdpCodec(pt, map.Name, map.Clock, map.Channels,
                    _fmtp.GetValueOrDefault(pt),
                    _feedback.TryGetValue(pt, out List<string>? fb) ? fb : []));
            }

            return codecs;
        }

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

        private void ParseFeedback(string value)
        {
            int space = value.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0 && int.TryParse(value[..space], out int pt))
            {
                (_feedback.TryGetValue(pt, out List<string>? list) ? list : _feedback[pt] = []).Add(value[(space + 1)..]);
            }
        }

        private void ParseSsrc(string value)
        {
            string[] parts = value.Split(' ', 2);
            if (uint.TryParse(parts[0], out uint ssrc))
            {
                Ssrc = ssrc;
                if (parts.Length == 2 && TryValue(parts[1], "cname:", out string cname))
                {
                    Cname = cname;
                }
            }
        }
    }
}
