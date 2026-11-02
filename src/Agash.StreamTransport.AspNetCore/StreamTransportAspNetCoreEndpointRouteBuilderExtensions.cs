using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Agash.StreamTransport;
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
        group.MapMethods(
            "",
            [HttpMethods.Get, HttpMethods.Head],
            (RequestDelegate)(protocol == "WHEP" ? WhepProfile : NoContent)
        );
        group.MapMethods(
            "/{resource}",
            [HttpMethods.Get, HttpMethods.Head],
            (RequestDelegate)ResourceNoContent
        );
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

        // RFC 9725 section 4.4.2: a WHIP offer is one MediaStream, so every section names it in msid.
        if (protocol == "WHIP" && OneMediaStream(parsed) is { } streams)
        {
            context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync(streams, cancellationToken).ConfigureAwait(false);
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

        // RFC 9725 section 4.4.3: WHIP takes the whole offer or refuses it, never a partial session.
        // WHEP section 4.3.4 lets the answer reject sections, but one audio or video section must stay.
        // An answer that does not mirror the offer section for section is this side's fault, and no
        // session stands on it (RFC 9429 section 5.3.1).
        if (
            !WebRtcSdp.SdpReader.TryParse(answer, out WebRtcSdp.SdpDescription answered)
            || !Mirrors(parsed, answered)
        )
        {
            _ = await resources.EndAsync(id).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        if (Unanswerable(parsed, answered, protocol) is { } refusal)
        {
            _ = await resources.EndAsync(id).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync(refusal, cancellationToken).ConfigureAwait(false);
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

    // Why the answer cannot stand, or null when it can: the offered sections it rejected.
    // Why the offer is not one MediaStream, or null when it is.
    private static string? OneMediaStream(WebRtcSdp.SdpDescription offer)
    {
        List<string?> streams =
        [
            .. offer.Media.Where(static m => !m.Rejected).Select(static m => m.Track?.StreamId),
        ];
        return streams.Contains(null)
                ? "Every section of a WHIP offer carries an msid naming its MediaStream."
            : streams.Distinct(StringComparer.Ordinal).Count() > 1
                ? "A WHIP offer carries one MediaStream; its sections name more than one in msid."
            : null;
    }

    // RFC 9429 section 5.3.1: the answer has the offer's sections, in its order, with its MIDs.
    private static bool Mirrors(WebRtcSdp.SdpDescription offer, WebRtcSdp.SdpDescription answer)
    {
        if (offer.Media.Length != answer.Media.Length)
        {
            return false;
        }

        for (int i = 0; i < offer.Media.Length; i++)
        {
            if (
                offer.Media[i].Kind != answer.Media[i].Kind
                || (!answer.Media[i].Rejected && offer.Media[i].Mid != answer.Media[i].Mid)
            )
            {
                return false;
            }
        }

        return true;
    }

    private static string? Unanswerable(
        WebRtcSdp.SdpDescription offer,
        WebRtcSdp.SdpDescription answered,
        string protocol
    )
    {
        int rejected = 0;
        int accepted = 0;
        for (int i = 0; i < offer.Media.Length; i++)
        {
            if (offer.Media[i].Rejected)
            {
                continue;
            }

            if (answered.Media[i].Rejected)
            {
                rejected++;
            }
            else if (
                answered.Media[i].Kind
                is WebRtcSdp.SdpMediaKind.Audio
                    or WebRtcSdp.SdpMediaKind.Video
            )
            {
                accepted++;
            }
        }

        return protocol == "WHIP" && rejected > 0
                ? $"{rejected} offered section(s) have no codec or setting this endpoint takes."
            : accepted == 0 ? "No offered audio or video section has a codec this endpoint takes."
            : null;
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
    // RFC 9725 section 4.2: the endpoint takes SDP offers. ICE servers go with the 201 to the POST (section
    // 4.6); a CORS preflight, which asks only what the endpoint allows, gets no TURN credentials minted.
    private static Task Options(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.Allow = "OPTIONS, POST, GET, HEAD";
        context.Response.Headers["Accept-Post"] = "application/sdp";
        if (!context.Request.Headers.ContainsKey("Access-Control-Request-Method"))
        {
            AdvertiseIceServers(context);
        }

        return Task.CompletedTask;
    }

    // RFC 9725 section 4.1: endpoints and sessions have no representation, so GET is a 2xx with no
    // content; an unknown session is not found.
    private static Task NoContent(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    }

    // WHEP section 4.2: a player finds a WHEP endpoint by HEAD, which answers 200 with application/sdp
    // and the WHEP profile link; GET answers the same, without a body either way.
    private static Task WhepProfile(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/sdp";
        context.Response.Headers.Link = "<urn:ietf:params:whep>; rel=\"profile\"";
        context.Response.ContentLength = 0;
        return Task.CompletedTask;
    }

    private static Task ResourceNoContent(HttpContext context)
    {
        context.Response.StatusCode = context
            .RequestServices.GetRequiredService<HttpMediaResources>()
            .Exists((string)context.Request.RouteValues["resource"]!)
            ? StatusCodes.Status204NoContent
            : StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    // What the session resource takes, for a browser's preflight before PATCH or DELETE (RFC 5789 section
    // 3.1).
    private static Task ResourceOptions(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.Allow = "OPTIONS, GET, HEAD, PATCH, DELETE";
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
