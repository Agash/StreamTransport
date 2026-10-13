using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Agash.StreamTransport.AspNetCore;
using Agash.StreamTransport.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using WebRtcSdp = Agash.StreamTransport.WebRtc.Sdp;

// Service registration lives in the namespace of IServiceCollection, as the framework's own does, so it
// is found without a using directive.
#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;

#pragma warning restore IDE0130

/// <summary>Registers WHIP and WHEP hosting in an ASP.NET Core application.</summary>
public static class StreamTransportAspNetCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers what <c>MapWhip</c> and <c>MapWhep</c> need: the sessions they keep for their resources,
    /// ended at shutdown. Media sessions themselves come from <c>AddStreamTransport()</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddHttpMediaEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<HttpMediaResources>();
        return services;
    }
}
