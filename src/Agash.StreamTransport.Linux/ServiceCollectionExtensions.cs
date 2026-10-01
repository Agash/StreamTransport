using Agash.StreamTransport.Linux.PipeWire;
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
    /// frames; V4L2 cameras; and PipeWire video and audio inputs and outputs over one shared daemon
    /// connection. They are reached through <c>MediaDevices</c>. Calling it again registers nothing more.
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
        services.TryAddSingleton<PipeWireConnection>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IVideoInputProvider, PipeWireVideoInputProvider>()
        );
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IVideoOutputProvider, PipeWireVideoOutputProvider>()
        );
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAudioInputProvider, PipeWireAudioInputProvider>()
        );
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAudioOutputProvider, PipeWireAudioOutputProvider>()
        );
        return services;
    }
}
