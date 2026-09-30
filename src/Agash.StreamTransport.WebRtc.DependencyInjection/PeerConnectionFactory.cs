using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.WebRtc.DependencyInjection;

/// <summary>
/// Creates <see cref="PeerConnection"/> instances with the registered certificate, clock, logging and a
/// congestion controller of their own. Resolve it from the container rather than constructing a peer
/// connection by hand, so every connection is wired the same way.
/// </summary>
/// <param name="services">The container, which supplies a new <see cref="INetworkController"/> per connection.</param>
/// <param name="certificate">The DTLS certificate every connection authenticates with.</param>
/// <param name="timeProvider">The clock every connection runs on.</param>
/// <param name="loggerFactory">The logging.</param>
public sealed class PeerConnectionFactory(
    IServiceProvider services,
    RtcCertificate certificate,
    TimeProvider timeProvider,
    ILoggerFactory? loggerFactory = null
)
{
    /// <summary>Creates a peer connection for the given media configuration.</summary>
    /// <param name="options">The media and ICE configuration.</param>
    /// <returns>The peer connection, which the caller disposes.</returns>
    public PeerConnection Create(PeerConnectionOptions options) =>
        new(
            options,
            certificate,
            loggerFactory,
            services.GetService<INetworkController>(),
            timeProvider
        );
}
