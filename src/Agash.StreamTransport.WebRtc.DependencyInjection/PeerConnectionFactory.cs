using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.WebRtc.DependencyInjection;

/// <summary>
/// Creates <see cref="PeerConnection"/> instances with the DI-registered DTLS factory and logging. Resolve
/// this from the container rather than constructing a peer connection by hand, so the DTLS-SRTP engine and
/// logger are wired consistently.
/// </summary>
public sealed class PeerConnectionFactory(IDtlsTransportFactory dtlsTransportFactory, ILoggerFactory? loggerFactory = null)
{
    /// <summary>Creates a peer connection for the given media configuration.</summary>
    public PeerConnection Create(PeerConnectionOptions options) =>
        new(options, dtlsTransportFactory, loggerFactory);
}
