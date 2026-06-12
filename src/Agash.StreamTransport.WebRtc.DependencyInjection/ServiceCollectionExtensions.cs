using Agash.StreamTransport.WebRtc.CongestionControl;
using Agash.StreamTransport.WebRtc.Dtls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Agash.StreamTransport.WebRtc.DependencyInjection;

/// <summary>DI registration for the Agash.StreamTransport WebRTC stack.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the WebRTC stack: the DTLS-SRTP factory (singleton, stable certificate/fingerprint), a
    /// <see cref="PeerConnectionFactory"/>, and a per-connection SCReAM <see cref="INetworkController"/>.
    /// </summary>
    public static IServiceCollection AddStreamTransportWebRtc(this IServiceCollection services, Action<ScreamOptions>? configureCongestionControl = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IDtlsTransportFactory, DtlsTransportFactory>();
        services.TryAddSingleton<PeerConnectionFactory>();

        // The controller is stateful per connection, so it is transient; options are bound from IOptions.
        services.TryAddTransient<INetworkController>(static sp =>
            new ScreamCongestionController(sp.GetService<IOptions<ScreamOptions>>()?.Value));

        if (configureCongestionControl is not null)
        {
            services.Configure(configureCongestionControl);
        }

        return services;
    }
}
