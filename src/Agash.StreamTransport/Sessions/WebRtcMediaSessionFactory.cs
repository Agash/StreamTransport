using Agash.StreamTransport.Media;
using Agash.StreamTransport.WebRtc.DependencyInjection;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.Sessions;

/// <summary>Makes media sessions over WebRTC peer connections.</summary>
/// <param name="codecs">The registered codecs and processors.</param>
/// <param name="payloadFormats">The registered RTP payload formats.</param>
/// <param name="connections">Makes peer connections.</param>
/// <param name="timeProvider">The clock every session runs on.</param>
/// <param name="loggerFactory">The logging.</param>
/// <param name="mobility">Re-probes connections when the host's networks change; null for none.</param>
public sealed class WebRtcMediaSessionFactory(
    MediaCodecRegistry codecs,
    RtpPayloadFormatRegistry payloadFormats,
    PeerConnectionFactory connections,
    TimeProvider timeProvider,
    ILoggerFactory? loggerFactory = null,
    MobilityEngine? mobility = null
) : IMediaSessionFactory
{
    private readonly SessionServices _services = new(
        codecs,
        payloadFormats,
        connections,
        new MediaClock(timeProvider),
        loggerFactory ?? NullLoggerFactory.Instance,
        mobility
    );

    /// <inheritdoc/>
    public IMediaSession Create(
        ISignalingChannel signaling,
        MediaSessionRole role,
        MediaEndpoints endpoints,
        MediaSessionOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(signaling);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);
        return new WebRtcMediaSession(signaling, role, endpoints, options, _services);
    }
}
