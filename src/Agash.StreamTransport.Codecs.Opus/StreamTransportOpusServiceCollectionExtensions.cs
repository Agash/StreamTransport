using Agash.StreamTransport.Codecs.Opus;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

// Service registration lives in the namespace of IServiceCollection, as the framework's own does, so it
// is found without a using directive.
#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;

#pragma warning restore IDE0130

/// <summary>DI registration for the Opus codec.</summary>
public static class StreamTransportOpusServiceCollectionExtensions
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
