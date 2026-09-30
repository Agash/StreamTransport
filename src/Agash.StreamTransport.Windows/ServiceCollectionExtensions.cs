using Agash.StreamTransport.Media;
using Agash.StreamTransport.Windows.Direct3D12;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agash.StreamTransport.Windows;

/// <summary>DI registration for the Windows media.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Direct3D 12 video processor, which sessions choose over CPU conversion for frames
    /// on the GPU. Spout and WASAPI sources and sinks are made by the application, which names them.
    /// Calling it again registers nothing more.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddWindowsMedia(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IVideoProcessorFactory, D3D12VideoProcessorFactory>()
        );
        return services;
    }
}
