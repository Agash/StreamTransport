using System.Globalization;
using System.Text;
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
            $"o=- {description.SessionId} 2 IN IP4 127.0.0.1\r\n"
        );
        sb.Append("s=-\r\n");
        sb.Append("t=0 0\r\n");
        if (description.IceLite)
        {
            sb.Append("a=ice-lite\r\n");
        }

        sb.Append("a=group:BUNDLE");
        foreach (SdpMediaDescription media in description.Media)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {media.Mid}");
        }

        sb.Append("\r\n");
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
            $"m={kind} {(media.Rejected ? 0 : 9)} UDP/TLS/RTP/SAVPF"
        );
        foreach (SdpCodec codec in media.Codecs)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {codec.PayloadType}");
        }

        sb.Append("\r\n");
        sb.Append("c=IN IP4 0.0.0.0\r\n");
        sb.Append("a=rtcp:9 IN IP4 0.0.0.0\r\n");
        if (media.RtcpMux)
        {
            sb.Append("a=rtcp-mux\r\n");
        }

        if (media.RtcpReducedSize)
        {
            sb.Append("a=rtcp-rsize\r\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"a=ice-ufrag:{media.IceUfrag}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"a=ice-pwd:{media.IcePwd}\r\n");
        sb.Append("a=ice-options:trickle\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"a=fingerprint:{media.Fingerprint}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"a=setup:{SetupText(media.Setup)}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"a=mid:{media.Mid}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"a={DirectionText(media.Direction)}\r\n");

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

            sb.Append(CultureInfo.InvariantCulture, $"a=ssrc:{ssrc} cname:{cname}\r\n");
            if (media.RtxSsrc is { } rtxSsrc)
            {
                sb.Append(CultureInfo.InvariantCulture, $"a=ssrc:{rtxSsrc} cname:{cname}\r\n");
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
