using Agash.StreamTransport.WebRtc.CongestionControl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Agash.StreamTransport.WebRtc.DependencyInjection;

/// <summary>DI registration for the Agash.StreamTransport WebRTC stack.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the WebRTC stack: the DTLS certificate (singleton, so the fingerprint is stable), a
    /// <see cref="PeerConnectionFactory"/>, and a per-connection SCReAM <see cref="INetworkController"/>.
    /// </summary>
    public static IServiceCollection AddStreamTransportWebRtc(
        this IServiceCollection services,
        Action<ScreamOptions>? configureCongestionControl = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(static _ => RtcCertificate.Generate());
        services.TryAddSingleton<PeerConnectionFactory>();

        // The controller is stateful per connection, so it is transient; options are bound from IOptions.
        services.TryAddTransient<INetworkController>(static sp => new ScreamCongestionController(
            sp.GetService<IOptions<ScreamOptions>>()?.Value
        ));

        if (configureCongestionControl is not null)
        {
            services.Configure(configureCongestionControl);
        }

        return services;
    }
}
