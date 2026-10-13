using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Agash.StreamTransport;
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
using Agash.StreamTransport.AspNetCore;

// Endpoint mapping lives in the namespace of IEndpointRouteBuilder, as the framework's own does, so it
// is found without a using directive.
#pragma warning disable IDE0130
namespace Microsoft.AspNetCore.Builder;

#pragma warning restore IDE0130

/// <summary>
/// Maps WHIP (RFC 9725) and WHEP endpoints onto the host's routing: a <c>POST</c> of an SDP offer makes a
/// session and answers with <c>201 Created</c>, the resource's URL and the SDP answer; a <c>DELETE</c> of
/// the resource ends it. When the host registers an <see cref="IIceServerProvider"/>, its STUN and TURN
/// servers are advertised as <c>Link</c> headers on <c>OPTIONS</c> and on the answer, so clients gather
/// with them. Candidates travel inside the offer and answer, and a client trickles the ones it gathers
/// later in a <c>PATCH</c> of the resource (RFC 9725 section 4.3.2): <c>204</c> once added, <c>428</c>
/// without the <c>If-Match</c> of the resource's ETag, <c>412</c> with another. An ICE restart in a
/// <c>PATCH</c> (new credentials, <c>If-Match: *</c>) gets <c>200</c> with the server's new credentials
/// and candidates and a new ETag (section 4.3.3). The returned group takes the host's conventions, such
/// as <c>RequireAuthorization</c> or <c>RequireCors</c>.
/// </summary>
public static class StreamTransportAspNetCoreEndpointRouteBuilderExtensions
{
    /// <summary>Maps a WHIP endpoint: publishers (OBS, a browser, another StreamTransport) send media in.</summary>
    /// <param name="endpoints">The host's route builder.</param>
    /// <param name="pattern">The endpoint's route, such as <c>/whip/{room}</c>.</param>
    /// <param name="handler">Supplies the sinks for each offer.</param>
    /// <returns>The group the endpoint and its resources live in.</returns>
    public static RouteGroupBuilder MapWhip(
        this IEndpointRouteBuilder endpoints,
        [StringSyntax("Route")] string pattern,
        HttpMediaHandler handler
    ) => Map(endpoints, pattern, handler, "WHIP");

    /// <summary>Maps a WHEP endpoint: players (a browser, another StreamTransport) take media out.</summary>
    /// <param name="endpoints">The host's route builder.</param>
    /// <param name="pattern">The endpoint's route, such as <c>/whep/{room}</c>.</param>
    /// <param name="handler">Supplies the sources for each offer.</param>
    /// <returns>The group the endpoint and its resources live in.</returns>
    public static RouteGroupBuilder MapWhep(
        this IEndpointRouteBuilder endpoints,
        [StringSyntax("Route")] string pattern,
        HttpMediaHandler handler
    ) => Map(endpoints, pattern, handler, "WHEP");

    private static RouteGroupBuilder Map(
        IEndpointRouteBuilder endpoints,
        string pattern,
        HttpMediaHandler handler,
        string protocol
    )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(handler);
        RouteGroupBuilder group = endpoints.MapGroup(pattern);
        group.MapPost("", (RequestDelegate)(context => OfferAsync(context, handler, protocol)));
        group.MapMethods("", [HttpMethods.Options], (RequestDelegate)Options);
        group.MapMethods("/{resource}", [HttpMethods.Options], (RequestDelegate)ResourceOptions);
        group.MapDelete("/{resource}", (RequestDelegate)DeleteAsync);
        group.MapMethods("/{resource}", [HttpMethods.Patch], (RequestDelegate)TrickleAsync);
        return group;
    }

    private static async Task OfferAsync(
        HttpContext context,
        HttpMediaHandler handler,
        string protocol
    )
    {
        if (
            !string.Equals(
                context.Request.ContentType?.Split(';')[0].Trim(),
                "application/sdp",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }

        CancellationToken cancellationToken = context.RequestAborted;
        if (context.Request.ContentLength > MaxOfferBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        string? offer = await ReadBoundedAsync(context.Request.Body, cancellationToken)
            .ConfigureAwait(false);
        if (offer is null)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        if (!WebRtcSdp.SdpReader.TryParse(offer, out WebRtcSdp.SdpDescription? parsed))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // RFC 9725 section 4.4.2: one audio and one video track at most.
        if (
            parsed.Media.Count(static m => !m.Rejected && m.Kind == WebRtcSdp.SdpMediaKind.Audio)
                > 1
            || parsed.Media.Count(static m => !m.Rejected && m.Kind == WebRtcSdp.SdpMediaKind.Video)
                > 1
        )
        {
            context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            context.Response.ContentType = "text/plain";
            await context
                .Response.WriteAsync(
                    "An offer carries at most one audio and one video section.",
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }

        HttpMediaSetup? setup = await handler(context, cancellationToken).ConfigureAwait(false);
        if (setup is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        IServiceProvider services = context.RequestServices;
        HttpMediaResources resources = services.GetRequiredService<HttpMediaResources>();
        string? answer = await resources
            .AnswerAsync(
                services.GetRequiredService<IMediaSessionFactory>(),
                setup,
                offer,
                protocol,
                out string id,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (answer is null)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        PathString location = context.Request.PathBase.Add(context.Request.Path).Add("/" + id);
        context.Response.StatusCode = StatusCodes.Status201Created;
        AdvertiseIceServers(context);
        context.Response.Headers.Location = location.ToString();
        context.Response.Headers.ETag = $"\"{id}\"";
        context.Response.ContentType = "application/sdp";
        await context.Response.WriteAsync(answer, cancellationToken).ConfigureAwait(false);
    }

    // An offer larger than this is refused: SDP for one audio and one video section is a few kilobytes.
    private const int MaxOfferBytes = 64 * 1024;

    private static async Task<string?> ReadBoundedAsync(
        Stream body,
        CancellationToken cancellationToken
    )
    {
        byte[] buffer = new byte[MaxOfferBytes + 1];
        int read = 0;
        int got;
        while (
            read < buffer.Length
            && (
                got = await body.ReadAsync(buffer.AsMemory(read), cancellationToken)
                    .ConfigureAwait(false)
            ) > 0
        )
        {
            read += got;
        }

        return read > MaxOfferBytes ? null : Encoding.UTF8.GetString(buffer, 0, read);
    }

    // RFC 9725 section 4.2: 200 with Accept-Post, and the ICE servers (section 4.6).
    private static Task Options(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers["Accept-Post"] = "application/sdp";
        AdvertiseIceServers(context);
        return Task.CompletedTask;
    }

    // What the session resource takes, for a browser's preflight before PATCH or DELETE (RFC 5789 section
    // 3.1).
    private static Task ResourceOptions(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.Allow = "OPTIONS, PATCH, DELETE";
        context.Response.Headers["Accept-Patch"] = TrickleIceFragment.MediaType;
        return Task.CompletedTask;
    }

    // The host's STUN and TURN servers, for the client to gather with (RFC 9725 section 4.6).
    private static void AdvertiseIceServers(HttpContext context)
    {
        if (context.RequestServices.GetService<IIceServerProvider>() is { } provider)
        {
            context.Response.Headers.Link = new([
                .. IceServerLinks.Format(provider.GetIceServersForPeer()),
            ]);
        }
    }

    private static async Task DeleteAsync(HttpContext context)
    {
        string resource = (string)context.Request.RouteValues["resource"]!;
        bool ended = await context
            .RequestServices.GetRequiredService<HttpMediaResources>()
            .EndAsync(resource)
            .ConfigureAwait(false);
        context.Response.StatusCode = ended
            ? StatusCodes.Status200OK
            : StatusCodes.Status404NotFound;
    }

    private static async Task TrickleAsync(HttpContext context)
    {
        if (
            !string.Equals(
                context.Request.ContentType?.Split(';')[0].Trim(),
                TrickleIceFragment.MediaType,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }

        string body;
        using (StreamReader reader = new(context.Request.Body, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        }

        (int status, string? tag, string? fragment) = await context
            .RequestServices.GetRequiredService<HttpMediaResources>()
            .TrickleAsync(
                (string)context.Request.RouteValues["resource"]!,
                context.Request.Headers.IfMatch,
                body,
                context.RequestAborted
            )
            .ConfigureAwait(false);
        context.Response.StatusCode = status;
        if (tag is not null && fragment is not null)
        {
            context.Response.Headers.ETag = $"\"{tag}\"";
            context.Response.ContentType = TrickleIceFragment.MediaType;
            await context
                .Response.WriteAsync(fragment, context.RequestAborted)
                .ConfigureAwait(false);
        }
    }
}
