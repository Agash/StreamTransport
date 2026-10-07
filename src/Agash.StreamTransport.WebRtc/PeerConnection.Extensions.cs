using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// RTP header extension negotiation for <see cref="PeerConnection"/> (RFC 8285): an offer maps the
/// extensions this endpoint understands; an answer keeps the offerer's identifier for each one it also
/// understands and leaves out the rest (section 7); only extensions both descriptions map are sent or read.
/// The identifiers hold for the whole BUNDLE group, so one map serves every section (RFC 8843 section 9.1).
/// </summary>
public sealed partial class PeerConnection
{
    private RtpExtensionMap _extensions = RtpExtensionMap.None;

    /// <summary>The header extensions the two sides agreed, empty before negotiation completes.</summary>
    public RtpExtensionMap NegotiatedExtensions => Volatile.Read(ref _extensions);

    // What an offer maps for a section: capture times for both kinds (lip sync), and for video the
    // per-frame timing and the playout delay the sender asks for.
    private static IReadOnlyList<SdpExtension> OfferedExtensions(SdpMediaKind kind)
    {
        RtpExtensionMap ids = RtpExtensionMap.Offered;
        return kind == SdpMediaKind.Video
            ?
            [
                new SdpExtension(ids.AbsoluteCaptureTime, RtpExtensionUris.AbsoluteCaptureTime),
                new SdpExtension(ids.VideoTiming, RtpExtensionUris.VideoTiming),
                new SdpExtension(ids.PlayoutDelay, RtpExtensionUris.PlayoutDelay),
            ]
            : [new SdpExtension(ids.AbsoluteCaptureTime, RtpExtensionUris.AbsoluteCaptureTime)];
    }

    // The answer's mappings: each offered extension this endpoint offers for the kind too, under the
    // offerer's identifier, with the direction mirrored (RFC 8285 section 7).
    private static IReadOnlyList<SdpExtension> AnsweredExtensions(SdpMediaDescription offer)
    {
        IReadOnlyList<SdpExtension> ours = OfferedExtensions(offer.Kind);
        List<SdpExtension> answered = [];
        foreach (SdpExtension offered in offer.Extensions)
        {
            if (
                offered.Id is >= 1 and <= 255
                && offered.Direction != SdpDirection.Inactive
                && ours.Any(o => o.Uri == offered.Uri)
            )
            {
                answered.Add(
                    offered with
                    {
                        Direction = offered.Direction switch
                        {
                            SdpDirection.SendOnly => SdpDirection.RecvOnly,
                            SdpDirection.RecvOnly => SdpDirection.SendOnly,
                            { } same => same,
                            null => null,
                        },
                    }
                );
            }
        }

        return answered;
    }

    // The agreed map: the extensions the answer kept, across every section it accepted.
    private void AgreeExtensions(SdpDescription answer) =>
        Volatile.Write(
            ref _extensions,
            RtpExtensionMap.From(answer.Media.Where(m => !m.Rejected).SelectMany(m => m.Extensions))
        );
}
