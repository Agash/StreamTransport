using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.WebRtc.DependencyInjection;

/// <summary>
/// Creates <see cref="PeerConnection"/> instances with the DI-registered certificate and logging. Resolve
/// this from the container rather than constructing a peer connection by hand, so the certificate and
/// logger are wired consistently.
/// </summary>
public sealed class PeerConnectionFactory(
    RtcCertificate certificate,
    ILoggerFactory? loggerFactory = null
)
{
    /// <summary>Creates a peer connection for the given media configuration.</summary>
    public PeerConnection Create(PeerConnectionOptions options) =>
        new(options, certificate, loggerFactory);
}
