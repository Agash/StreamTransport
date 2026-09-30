using Agash.StreamTransport.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.Codecs.FFmpeg;

/// <summary>DI registration for the FFmpeg codecs.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers an encoder and a decoder factory for every backend this platform has, and the swscale
    /// processor. Calling it again registers nothing more.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">Shared options; the container's logging is used when they name none.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddFFmpegCodecs(
        this IServiceCollection services,
        FFmpegCodecOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(static d => d.ServiceType == typeof(Registered)))
        {
            return services;
        }

        services.AddSingleton<Registered>();
        FFmpegCodecOptions Options(IServiceProvider provider) =>
            options?.LoggerFactory is not null
                ? options
                : new FFmpegCodecOptions
                {
                    LoggerFactory = provider.GetService<ILoggerFactory>(),
                    EncoderOptions = options?.EncoderOptions ?? [],
                };

        foreach (EncoderBackend backend in Enum.GetValues<EncoderBackend>())
        {
            services.AddSingleton<IVideoEncoderFactory>(provider => new FFmpegVideoEncoderFactory(
                backend,
                Options(provider)
            ));
        }

        foreach (DecoderBackend backend in Enum.GetValues<DecoderBackend>())
        {
            services.AddSingleton<IVideoDecoderFactory>(provider => new FFmpegVideoDecoderFactory(
                backend,
                Options(provider)
            ));
        }

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IVideoProcessorFactory, FFmpegVideoProcessorFactory>()
        );
        return services;
    }

    // Marks the registration as done.
    private sealed class Registered;
}
