using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.WebRtc.Transport;

/// <summary>Makes WebRTC transports for media sessions.</summary>
/// <param name="connections">Makes the peer connections.</param>
/// <param name="payloadFormats">The RTP payload formats codecs are carried in.</param>
/// <param name="loggerFactory">Logging; none when null.</param>
/// <param name="mobility">Re-probes connections when the host's networks change; none when null.</param>
public sealed class WebRtcMediaTransportFactory(
    PeerConnectionFactory connections,
    RtpPayloadFormatRegistry payloadFormats,
    ILoggerFactory? loggerFactory = null,
    MobilityEngine? mobility = null
) : IMediaTransportFactory
{
    private readonly WebRtcTransportServices _services = new(
        connections,
        payloadFormats,
        loggerFactory ?? NullLoggerFactory.Instance,
        mobility
    );

    /// <inheritdoc/>
    public IMediaTransport Create(
        ISignalingChannel signaling,
        MediaSessionRole role,
        MediaTransportOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(signaling);
        ArgumentNullException.ThrowIfNull(options);
        WebRtcTransportOptions webRtc =
            options as WebRtcTransportOptions
            ?? new WebRtcTransportOptions
            {
                IceServers = options.IceServers,
                ForwardErrorCorrection = options.ForwardErrorCorrection,
            };
        return new WebRtcMediaTransport(signaling, role, webRtc, _services);
    }
}
