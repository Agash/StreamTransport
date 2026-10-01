using Agash.StreamTransport.Sessions;
using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agash.StreamTransport;

/// <summary>DI registration for StreamTransport media sessions.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers media sessions over WebRTC: the WebRTC stack with its payload formats, the codec
    /// registry built from every registered encoder, decoder and processor factory, network mobility, and
    /// the <see cref="IMediaSessionFactory"/>. Codecs come from their own packages' registrations, such as
    /// <c>AddFFmpegCodecs()</c> and <c>AddOpusCodecs()</c>, or from the host's own factories.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddStreamTransport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddStreamTransportWebRtc();
        services.TryAddSingleton<MediaCodecRegistry>();
        services.TryAddSingleton<MediaDevices>();
        services.TryAddSingleton<INetworkMonitor, NetworkChangeMonitor>();
        services.TryAddSingleton<MobilityEngine>();
        services.TryAddSingleton<IMediaSessionFactory, WebRtcMediaSessionFactory>();
        return services;
    }
}
