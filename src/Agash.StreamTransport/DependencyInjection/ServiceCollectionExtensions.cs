using System.Diagnostics.CodeAnalysis;
using Agash.StreamTransport.Codecs;
using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.CongestionControl;
using Agash.StreamTransport.WebRtc.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agash.StreamTransport.DependencyInjection;

/// <summary>Dependency-injection registration for the StreamTransport media transport.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the full default stack: the transport-agnostic core (<see cref="AddStreamTransportCore"/>)
    /// plus the built-in WebRTC wire transport (<see cref="AddWebRtcMediaTransport"/>). Batteries-included
    /// entry point. To run the same orchestration over a different wire transport, call
    /// <see cref="AddStreamTransportCore"/> and register your own <see cref="IMediaTransport"/> instead of
    /// this method.
    /// </summary>
    public static IServiceCollection AddStreamTransport(
        this IServiceCollection services,
        Action<ScreamOptions>? configureCongestionControl = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddStreamTransportCore();
        services.AddWebRtcMediaTransport(configureCongestionControl);
        return services;
    }

    /// <summary>
    /// Registers the transport-agnostic core: the codec <see cref="IMediaCodecRegistry"/> with the built-in
    /// HEVC + Opus descriptors (add more via <see cref="AddVideoCodec{T}"/> / <see cref="AddAudioCodec{T}"/>)
    /// and the <see cref="MediaSessionFactory"/>
    /// that composes <see cref="MediaPublisher"/> / <see cref="MediaSubscriber"/> from an
    /// <see cref="IMediaTransport"/> and the host's <see cref="Microsoft.Extensions.Logging.ILoggerFactory"/>.
    /// Pair with a wire-transport registration (<see cref="AddWebRtcMediaTransport"/> or your own
    /// <see cref="IMediaTransport"/>).
    /// </summary>
    public static IServiceCollection AddStreamTransportCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IMediaCodecRegistry, MediaCodecRegistry>();
        services.TryAddSingleton<MediaSessionFactory>();

        // Network awareness + mobility: the engine watches interfaces and triggers per-connection
        // path recovery proactively on a change.
        services.TryAddSingleton<INetworkMonitor, NetworkChangeMonitor>();
        services.TryAddSingleton<MobilityEngine>();

        // Built-in codecs: HEVC video + Opus audio. TryAddEnumerable so they register once even if
        // AddStreamTransport is called more than once, and so a host's own descriptors add alongside them.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IVideoCodecDescriptor, H265VideoCodecDescriptor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAudioCodecDescriptor, OpusAudioCodecDescriptor>());
        return services;
    }

    /// <summary>
    /// Register an additional video codec. Its descriptor is added to the <see cref="IMediaCodecRegistry"/>,
    /// so it is offered in SDP and selectable by a peer with no edits to the negotiation code.
    /// </summary>
    public static IServiceCollection AddVideoCodec<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(this IServiceCollection services)
        where T : class, IVideoCodecDescriptor
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IVideoCodecDescriptor, T>();
        return services;
    }

    /// <summary>Register an additional audio codec (the audio counterpart of <see cref="AddVideoCodec{T}"/>).</summary>
    public static IServiceCollection AddAudioCodec<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(this IServiceCollection services)
        where T : class, IAudioCodecDescriptor
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IAudioCodecDescriptor, T>();
        return services;
    }

    /// <summary>
    /// Registers the built-in WebRTC <see cref="IMediaTransport"/> and the WebRTC stack it needs (DTLS-SRTP
    /// factory, SCReAM congestion control, peer-connection factory). Uses <c>TryAdd</c>, so a consumer that
    /// registers its own <see cref="IMediaTransport"/> first keeps it.
    /// </summary>
    public static IServiceCollection AddWebRtcMediaTransport(
        this IServiceCollection services,
        Action<ScreamOptions>? configureCongestionControl = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddStreamTransportWebRtc(configureCongestionControl);

        // A factory so each WebRTC sender resolves its own per-connection congestion controller (transient).
        services.TryAddSingleton<Func<INetworkController>>(sp => sp.GetRequiredService<INetworkController>);
        services.TryAddSingleton<IMediaTransport, WebRtcMediaTransport>();
        return services;
    }
}
