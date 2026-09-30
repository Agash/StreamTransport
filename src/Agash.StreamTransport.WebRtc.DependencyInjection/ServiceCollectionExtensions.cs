using Agash.StreamTransport.WebRtc.CongestionControl;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Agash.StreamTransport.WebRtc.DependencyInjection;

/// <summary>DI registration for the Agash.StreamTransport WebRTC stack.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the WebRTC stack: the system <see cref="TimeProvider"/> unless one is registered, the DTLS
    /// certificate (singleton, so the fingerprint is stable), a
    /// <see cref="PeerConnectionFactory"/>, a per-connection SCReAM <see cref="INetworkController"/>, and
    /// the RTP payload formats this library implements with the <see cref="RtpPayloadFormatRegistry"/>
    /// built from every registered <see cref="RtpPayloadFormat"/>.
    /// </summary>
    public static IServiceCollection AddStreamTransportWebRtc(
        this IServiceCollection services,
        Action<ScreamOptions>? configureCongestionControl = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(static _ => RtcCertificate.Generate());
        services.TryAddSingleton<PeerConnectionFactory>();

        // The controller is stateful per connection, so it is transient; options are bound from IOptions.
        services.TryAddTransient<INetworkController>(static sp => new ScreamCongestionController(
            sp.GetService<IOptions<ScreamOptions>>()?.Value
        ));

        foreach (RtpPayloadFormat format in RtpPayloadFormatRegistry.BuiltIn.Formats)
        {
            services.AddRtpPayloadFormat(format);
        }

        // The built-in formats go first, so a host's own format for the same encoding name replaces them
        // whichever order the registrations ran in.
        services.TryAddSingleton(static sp => new RtpPayloadFormatRegistry(
            sp.GetServices<RtpPayloadFormat>()
                .OrderBy(static f => RtpPayloadFormatRegistry.BuiltIn.Formats.Contains(f) ? 0 : 1)
        ));

        if (configureCongestionControl is not null)
        {
            services.Configure(configureCongestionControl);
        }

        return services;
    }

    /// <summary>
    /// Registers an RTP payload format. It replaces a built-in format with the same encoding name, and a
    /// format registered after another host format of that name replaces it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="format">The format.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddRtpPayloadFormat(
        this IServiceCollection services,
        RtpPayloadFormat format
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(format);
        services.TryAddEnumerable(ServiceDescriptor.Singleton(format));
        return services;
    }
}
