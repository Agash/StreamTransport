using Agash.StreamTransport.Media;
using FFmpeg.Interop;
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
        FFmpegCodecOptions Options(IServiceProvider provider)
        {
            FFmpegCodecOptions resolved = options?.LoggerFactory is not null
                ? options
                : new FFmpegCodecOptions
                {
                    LoggerFactory = provider.GetService<ILoggerFactory>(),
                    EncoderOptions = options?.EncoderOptions ?? [],
                    RouteFFmpegLog = options?.RouteFFmpegLog ?? true,
                };
            provider.GetRequiredService<Registered>().RouteLog(resolved);
            return resolved;
        }

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
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IVideoInputProvider, FFmpegVideoInputProvider>()
        );
        return services;
    }

    // Marks the registration as done, and routes FFmpeg's log once per container until it is disposed.
    private sealed class Registered : IDisposable
    {
        private readonly Lock _gate = new();
        private IDisposable? _route;
        private bool _disposed;

        public void RouteLog(FFmpegCodecOptions options)
        {
            if (!options.RouteFFmpegLog || options.LoggerFactory is not { } factory)
            {
                return;
            }

            lock (_gate)
            {
                if (!_disposed)
                {
                    _route ??= FFmpegLogging.RouteTo(factory);
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _route?.Dispose();
                _route = null;
            }
        }
    }
}
