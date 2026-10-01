using Agash.StreamTransport.MacOS.Metal;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agash.StreamTransport.MacOS;

/// <summary>DI registration for the macOS media.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Metal video processor, which sessions choose over CPU conversion for IOSurface
    /// frames, and Syphon video and Core Audio inputs and outputs, reached through <c>MediaDevices</c>.
    /// Calling it again registers nothing more.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddMacOSMedia(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IVideoProcessorFactory, MetalVideoProcessorFactory>()
        );
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IVideoInputProvider, SyphonVideoInputProvider>()
        );
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IVideoOutputProvider, SyphonVideoOutputProvider>()
        );
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAudioInputProvider, CoreAudioInputProvider>()
        );
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAudioOutputProvider, CoreAudioOutputProvider>()
        );
        return services;
    }
}
