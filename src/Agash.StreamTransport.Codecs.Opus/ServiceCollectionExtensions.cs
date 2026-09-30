using Agash.StreamTransport.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agash.StreamTransport.Codecs.Opus;

/// <summary>DI registration for the Opus codec.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the Opus encoder and decoder factories. Calling it again registers nothing more.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">Opus settings for every encoder; defaults when null.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddOpusCodecs(
        this IServiceCollection services,
        OpusOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAudioEncoderFactory, OpusAudioEncoderFactory>(
                _ => new OpusAudioEncoderFactory(options)
            )
        );
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAudioDecoderFactory, OpusAudioDecoderFactory>()
        );
        return services;
    }
}
