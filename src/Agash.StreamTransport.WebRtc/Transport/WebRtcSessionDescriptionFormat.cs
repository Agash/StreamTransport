using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Transport;

/// <summary>
/// WebRTC session descriptions: SDP read by <see cref="SdpReader"/>, as the peer connection reads it. An
/// offer carries ICE credentials and a fingerprint in every section it does not reject, and setup actpass
/// (RFC 9429 section 5.2.1, RFC 8842 section 5.2); an answer carries the same with setup active or passive
/// (RFC 9429 section 5.3.1, RFC 4145 section 4.1).
/// </summary>
public sealed class WebRtcSessionDescriptionFormat : ISessionDescriptionFormat
{
    private WebRtcSessionDescriptionFormat() { }

    /// <summary>The format.</summary>
    public static WebRtcSessionDescriptionFormat Instance { get; } = new();

    /// <inheritdoc/>
    public string MediaType => "application/sdp";

    /// <inheritdoc/>
    public bool IsValid(SessionDescription description)
    {
        if (
            !SdpReader.TryParse(description.Sdp, out SdpDescription parsed)
            || parsed.Media.Count == 0
        )
        {
            return false;
        }

        bool offer = description.Kind == SdpKind.Offer;
        foreach (SdpMediaDescription section in parsed.Media)
        {
            if (section.Rejected)
            {
                continue;
            }

            if (
                string.IsNullOrEmpty(section.IceUfrag)
                || string.IsNullOrEmpty(section.IcePwd)
                || section.Fingerprints.Count == 0
                || (section.Setup == SdpSetup.ActPass) != offer
            )
            {
                return false;
            }
        }

        return true;
    }
}
