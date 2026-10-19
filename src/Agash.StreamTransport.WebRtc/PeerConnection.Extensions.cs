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
    private bool _sendCryptex;

    /// <summary>The header extensions the two sides agreed, empty before negotiation completes.</summary>
    public RtpExtensionMap NegotiatedExtensions => Volatile.Read(ref _extensions);

    // RFC 6464 section 4: this endpoint runs no voice activity detector, so it says the V bit is unused.
    private const string NoVoiceActivity = "vad=off";

    // What an offer maps for a section: capture times for both kinds (lip sync), the transport-wide
    // sequence number transport feedback needs and the MID every bundled section maps (RFC 9143 section
    // 9.1); for audio the audio level (RFC 8834 section 5.2.2), and for video the orientation (section
    // 5.2.5), the per-frame timing and the playout delay the sender asks for.
    private static IReadOnlyList<SdpExtension> OfferedExtensions(SdpMediaKind kind)
    {
        RtpExtensionMap ids = RtpExtensionMap.Offered;
        return kind == SdpMediaKind.Video
            ?
            [
                new SdpExtension(ids.AbsoluteCaptureTime, RtpExtensionUris.AbsoluteCaptureTime),
                new SdpExtension(ids.VideoTiming, RtpExtensionUris.VideoTiming),
                new SdpExtension(ids.PlayoutDelay, RtpExtensionUris.PlayoutDelay),
                new SdpExtension(ids.TransportSequence, RtpExtensionUris.TransportWideSequence),
                new SdpExtension(ids.Mid, RtpExtensionUris.Mid),
                new SdpExtension(ids.VideoOrientation, RtpExtensionUris.VideoOrientation),
            ]
            :
            [
                new SdpExtension(ids.AbsoluteCaptureTime, RtpExtensionUris.AbsoluteCaptureTime),
                new SdpExtension(ids.TransportSequence, RtpExtensionUris.TransportWideSequence),
                new SdpExtension(ids.Mid, RtpExtensionUris.Mid),
                new SdpExtension(
                    ids.AudioLevel,
                    RtpExtensionUris.AudioLevel,
                    Attributes: NoVoiceActivity
                ),
            ];
    }

    /// <summary>
    /// Whether the peer sets the audio level's V bit: its mapping does not say <c>vad=off</c>, the default
    /// being on (RFC 6464 section 4). False before a description arrives.
    /// </summary>
    internal bool PeerSignalsVoiceActivity { get; private set; }

    // RFC 9335 section 4: Cryptex goes out only when the peer receives it in every RTP section, which BUNDLE
    // requires of it anyway; packets this side reads carry their own marking.
    private void NoteCryptex(SdpDescription remote)
    {
        List<SdpMediaDescription> rtp =
        [
            .. remote.Media.Where(static m => !m.Rejected && m.Codecs.Count > 0),
        ];
        _sendCryptex = _options.Cryptex && rtp.Count > 0 && rtp.TrueForAll(static m => m.Cryptex);
        if (_srtp is { } srtp)
        {
            srtp.SendCryptex = _sendCryptex;
        }
    }

    private void NoteVoiceActivity(SdpDescription remote) =>
        PeerSignalsVoiceActivity = remote.Media.Any(m =>
            m.Extensions.Any(e =>
                e.Uri == RtpExtensionUris.AudioLevel
                && !string.Equals(e.Attributes?.Trim(), NoVoiceActivity, StringComparison.Ordinal)
            )
        );

    // Transport feedback is answered only where the offer did not also offer RFC 8888, which is preferred.
    private static bool AnswersTransportFeedback(SdpMediaDescription offer) =>
        offer.TransportWideFeedback && !offer.CongestionControlFeedback;

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
                && (
                    offered.Uri != RtpExtensionUris.TransportWideSequence
                    || AnswersTransportFeedback(offer)
                )
            )
            {
                answered.Add(
                    offered with
                    {
                        // The attributes describe what this side sends: its own, never the offerer's.
                        Attributes = ours.First(o => o.Uri == offered.Uri).Attributes,
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
