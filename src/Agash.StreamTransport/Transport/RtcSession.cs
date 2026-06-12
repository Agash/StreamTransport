using Agash.StreamTransport;
using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.Sdp;
using Microsoft.Extensions.Logging;
using WebRtcIceCandidate = Agash.StreamTransport.WebRtc.Ice.IceCandidate;

namespace Agash.StreamTransport.Transport;

/// <summary>
/// Bridges the <see cref="PeerConnection"/> to an <see cref="ISignalingChannel"/>: it converts the
/// structured offer/answer to and from the SDP strings the channel carries (<see cref="SdpWriter"/> /
/// <see cref="SdpReader"/>) and the candidate forms, and runs the offer/answer handshake. Media lines are
/// fixed at construction, so there is no track-timing race: the answer always reflects the configured media.
/// </summary>
internal sealed partial class RtcSession : IAsyncDisposable
{
    private readonly ISignalingChannel _signaling;
    private readonly ILogger _logger;

    /// <summary>
    /// Create a session over <paramref name="signaling"/>. The DTLS-SRTP engine is supplied as
    /// <paramref name="dtlsFactory"/> (the pluggable seam - BouncyCastle today, a first-party BCL stack later)
    /// rather than constructed here, so the whole peer connection is built from injected services.
    /// </summary>
    public RtcSession(
        ISignalingChannel signaling,
        PeerConnectionOptions peerOptions,
        IDtlsTransportFactory dtlsFactory,
        ILoggerFactory loggerFactory,
        INetworkController? controller = null)
    {
        _signaling = signaling;
        _logger = loggerFactory.CreateLogger<RtcSession>();
        Pc = new PeerConnection(peerOptions, dtlsFactory, loggerFactory, controller);
        Pc.LocalIceCandidate += OnLocalIceCandidate;
    }

    /// <summary>The underlying first-party peer connection.</summary>
    public PeerConnection Pc { get; }

    /// <summary>Begin consuming the remote description/ICE. Call once after media handlers are wired.</summary>
    public void Listen()
    {
        _signaling.DescriptionReceived += OnRemoteDescriptionAsync;
        _signaling.IceCandidateReceived += OnRemoteIceCandidateAsync;
    }

    /// <summary>Create the offer and send it over the signaling channel.</summary>
    public async Task StartAsOffererAsync(CancellationToken cancellationToken)
    {
        SdpDescription offer = Pc.CreateOffer();
        LogOfferSent();
        await _signaling.SendAsync(new SessionDescription(SdpKind.Offer, SdpWriter.Write(offer)), cancellationToken).ConfigureAwait(false);
    }

    private async Task OnRemoteDescriptionAsync(SessionDescription description)
    {
        if (!SdpReader.TryParse(description.Sdp, out SdpDescription parsed))
        {
            LogUnparsableDescription(description.Kind);
            return;
        }

        Pc.SetRemoteDescription(parsed, description.Kind == SdpKind.Offer ? SdpType.Offer : SdpType.Answer);
        LogRemoteDescription(description.Kind);

        if (description.Kind == SdpKind.Offer)
        {
            SdpDescription answer = Pc.CreateAnswer();
            LogAnswerSent();
            await _signaling.SendAsync(new SessionDescription(SdpKind.Answer, SdpWriter.Write(answer))).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Offer sent.")]
    private partial void LogOfferSent();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Answer sent.")]
    private partial void LogAnswerSent();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Applied remote {Kind} description.")]
    private partial void LogRemoteDescription(SdpKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discarded unparsable remote {Kind} description.")]
    private partial void LogUnparsableDescription(SdpKind kind);

    private Task OnRemoteIceCandidateAsync(IceCandidate candidate)
    {
        if (WebRtcIceCandidate.TryParse(candidate.Candidate, out WebRtcIceCandidate parsed))
        {
            Pc.AddRemoteIceCandidate(parsed);
        }

        return Task.CompletedTask;
    }

    private void OnLocalIceCandidate(WebRtcIceCandidate candidate) =>
        _ = _signaling.SendAsync(new IceCandidate(candidate.ToSdp(), candidate.ComponentId.ToString(System.Globalization.CultureInfo.InvariantCulture), 0));

    public async ValueTask DisposeAsync()
    {
        _signaling.DescriptionReceived -= OnRemoteDescriptionAsync;
        _signaling.IceCandidateReceived -= OnRemoteIceCandidateAsync;
        await Pc.DisposeAsync().ConfigureAwait(false);
    }
}
