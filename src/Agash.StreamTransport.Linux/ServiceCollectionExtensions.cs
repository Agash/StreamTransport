using Agash.StreamTransport.Linux.V4l2;
using Agash.StreamTransport.Linux.Vulkan;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agash.StreamTransport.Linux;

/// <summary>DI registration for the Linux media.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Vulkan video processor, which sessions choose over CPU conversion for DMA-BUF
    /// frames. PipeWire sources and sinks are made by the application, which names them and owns the
    /// connection to the daemon. Calling it again registers nothing more.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddLinuxMedia(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IVideoProcessorFactory, VulkanVideoProcessorFactory>()
        );
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IVideoInputProvider, V4l2VideoInputProvider>()
        );
        return services;
    }
}
