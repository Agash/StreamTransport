using System.Globalization;
using System.Text;
using Agash.StreamTransport.WebRtc.Ice;
using Dtls.Core;

namespace Agash.StreamTransport.WebRtc.Sdp;

/// <summary>Serializes a <see cref="SdpDescription"/> to the SDP text a WebRTC peer (including a browser) expects.</summary>
public static class SdpWriter
{
    /// <summary>Renders the session description as SDP (CRLF-terminated lines, RFC 8866).</summary>
    public static string Write(SdpDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        var sb = new StringBuilder();

        sb.Append("v=0\r\n");
        sb.Append(
            CultureInfo.InvariantCulture,
            $"o=- {description.SessionId} {description.SessionVersion} IN IP4 127.0.0.1\r\n"
        );
        sb.Append("s=-\r\n");
        sb.Append("t=0 0\r\n");
        if (description.IceLite)
        {
            sb.Append("a=ice-lite\r\n");
        }

        if (description.BundleGroup is { Count: > 0 } bundled)
        {
            sb.Append("a=group:BUNDLE ").Append(string.Join(' ', bundled)).Append("\r\n");
        }

        sb.Append("a=msid-semantic: WMS *\r\n");

        foreach (SdpMediaDescription media in description.Media)
        {
            WriteMedia(sb, media);
        }

        return sb.ToString();
    }

    private static void WriteMedia(StringBuilder sb, SdpMediaDescription media)
    {
        string kind = media.Kind switch
        {
            SdpMediaKind.Audio => "audio",
            SdpMediaKind.Video => "video",
            _ => "application",
        };

        sb.Append(
            CultureInfo.InvariantCulture,
            $"m={kind} {(media.Rejected || media.BundleOnly ? 0 : 9)} {media.Protocol}"
        );
        foreach (SdpCodec codec in media.Codecs)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {codec.PayloadType}");
        }

        if (media.Codecs.Count == 0)
        {
            foreach (string format in media.Formats)
            {
                sb.Append(' ').Append(format);
            }
        }

        // RFC 8866 section 5.14: an m= line lists at least one format, a rejected one with nothing to list too.
        if (media.Codecs.Count == 0 && media.Formats.Count == 0)
        {
            sb.Append(" 0");
        }

        sb.Append("\r\n");
        sb.Append("c=IN IP4 0.0.0.0\r\n");
        sb.Append("a=rtcp:9 IN IP4 0.0.0.0\r\n");
        if (media.RtcpMux)
        {
            sb.Append("a=rtcp-mux\r\n");
        }

        if (media.RtcpMuxOnly)
        {
            sb.Append("a=rtcp-mux-only\r\n");
        }

        if (media.BundleOnly)
        {
            sb.Append("a=bundle-only\r\n");
        }

        if (media.RtcpReducedSize)
        {
            sb.Append("a=rtcp-rsize\r\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"a=ice-ufrag:{media.IceUfrag}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"a=ice-pwd:{media.IcePwd}\r\n");
        if (media.IceOptions != IceOptions.None)
        {
            sb.Append(
                CultureInfo.InvariantCulture,
                $"a=ice-options:{IceOptionsText.Format(media.IceOptions)}\r\n"
            );
        }
        sb.Append(CultureInfo.InvariantCulture, $"a=fingerprint:{media.Fingerprint}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"a=setup:{SetupText(media.Setup)}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"a=mid:{media.Mid}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"a={DirectionText(media.Direction)}\r\n");
        if (media.MediaStreamId is { } stream && media.TrackId is { } track)
        {
            sb.Append(CultureInfo.InvariantCulture, $"a=msid:{stream} {track}\r\n");
        }

        foreach (SdpCodec codec in media.Codecs)
        {
            sb.Append(
                CultureInfo.InvariantCulture,
                $"a=rtpmap:{codec.PayloadType} {codec.EncodingName}/{codec.ClockRate}"
            );
            if (codec.Channels is { } channels)
            {
                sb.Append(CultureInfo.InvariantCulture, $"/{channels}");
            }

            sb.Append("\r\n");
            foreach (string feedback in codec.RtcpFeedback)
            {
                sb.Append(
                    CultureInfo.InvariantCulture,
                    $"a=rtcp-fb:{codec.PayloadType} {feedback}\r\n"
                );
            }

            if (media.TransportWideFeedback)
            {
                sb.Append(
                    CultureInfo.InvariantCulture,
                    $"a=rtcp-fb:{codec.PayloadType} transport-cc\r\n"
                );
            }

            if (!string.IsNullOrEmpty(codec.FormatParameters))
            {
                sb.Append(
                    CultureInfo.InvariantCulture,
                    $"a=fmtp:{codec.PayloadType} {codec.FormatParameters}\r\n"
                );
            }
        }

        if (media.CongestionControlFeedback)
        {
            sb.Append("a=rtcp-fb:* ack ccfb\r\n");
        }

        if (media.ExtmapAllowMixed)
        {
            sb.Append("a=extmap-allow-mixed\r\n");
        }

        foreach (SdpExtension extension in media.Extensions)
        {
            sb.Append(CultureInfo.InvariantCulture, $"a=extmap:{extension.Id}");
            if (extension.Direction is { } direction)
            {
                sb.Append('/').Append(DirectionText(direction));
            }

            sb.Append(' ').Append(extension.Uri).Append("\r\n");
        }

        if (media.Ecn is { } ecn && !ecn.Methods.IsEmpty)
        {
            sb.Append("a=ecn-capable-rtp: ").Append(string.Join(',', ecn.Methods));
            List<string> parameters = [];
            if (ecn.Mode is { } mode)
            {
                parameters.Add($"mode={mode}");
            }

            if (ecn.Ect is { } ect)
            {
                parameters.Add($"ect={ect}");
            }

            if (parameters.Count > 0)
            {
                sb.Append(' ').Append(string.Join("; ", parameters));
            }

            sb.Append("\r\n");
        }

        foreach (Ice.IceCandidate candidate in media.Candidates)
        {
            sb.Append(CultureInfo.InvariantCulture, $"a={candidate.ToSdp()}\r\n");
        }

        if (media.EndOfCandidates)
        {
            sb.Append("a=end-of-candidates\r\n");
        }

        if (media.Ssrc is { } ssrc)
        {
            string cname = media.Cname ?? "streamtransport";
            if (media.RtxSsrc is { } rtx)
            {
                sb.Append(CultureInfo.InvariantCulture, $"a=ssrc-group:FID {ssrc} {rtx}\r\n");
            }

            if (media.FecSsrc is { } fec)
            {
                sb.Append(CultureInfo.InvariantCulture, $"a=ssrc-group:FEC-FR {ssrc} {fec}\r\n");
            }

            sb.Append(CultureInfo.InvariantCulture, $"a=ssrc:{ssrc} cname:{cname}\r\n");
            if (media.RtxSsrc is { } rtxSsrc)
            {
                sb.Append(CultureInfo.InvariantCulture, $"a=ssrc:{rtxSsrc} cname:{cname}\r\n");
            }

            if (media.FecSsrc is { } fecSsrc)
            {
                sb.Append(CultureInfo.InvariantCulture, $"a=ssrc:{fecSsrc} cname:{cname}\r\n");
            }
        }
    }

    private static string SetupText(SdpSetup setup) =>
        setup switch
        {
            SdpSetup.Active => "active",
            SdpSetup.Passive => "passive",
            _ => "actpass",
        };

    private static string DirectionText(SdpDirection direction) =>
        direction switch
        {
            SdpDirection.SendOnly => "sendonly",
            SdpDirection.RecvOnly => "recvonly",
            SdpDirection.Inactive => "inactive",
            _ => "sendrecv",
        };
}
