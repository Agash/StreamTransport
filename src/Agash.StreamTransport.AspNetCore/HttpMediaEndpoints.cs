using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
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

namespace Agash.StreamTransport.AspNetCore;

/// <summary>What a session made for an HTTP offer sends and receives, and how it is set up.</summary>
/// <param name="Endpoints">
/// For WHIP ingest, the sinks the publisher's media goes to; for WHEP playback, the sources the player
/// receives.
/// </param>
/// <param name="Options">How the session is set up.</param>
public sealed record HttpMediaSetup(MediaEndpoints Endpoints, MediaSessionOptions Options)
{
    /// <summary>Called once when the session ends, by the client's DELETE, a failure or shutdown.</summary>
    public Func<ValueTask>? Ended { get; init; }
}

/// <summary>
/// Decides what a WHIP or WHEP offer gets: its endpoints and options, or null to refuse it with
/// <c>404 Not Found</c>. Authentication and authorization are the host's, applied to the mapped group.
/// </summary>
/// <param name="context">The request, after the host's middleware.</param>
/// <param name="cancellationToken">The request's cancellation.</param>
/// <returns>The setup, or null.</returns>
public delegate ValueTask<HttpMediaSetup?> HttpMediaHandler(
    HttpContext context,
    CancellationToken cancellationToken
);

/// <summary>Registers WHIP and WHEP hosting in an ASP.NET Core application.</summary>
public static class HttpMediaServiceCollectionExtensions
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
public static class HttpMediaEndpointRouteBuilderExtensions
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
        string offer;
        using (StreamReader reader = new(context.Request.Body, Encoding.UTF8))
        {
            offer = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!WebRtcSdp.SdpReader.TryParse(offer, out _))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
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

    private static Task Options(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        context.Response.Headers["Accept-Post"] = "application/sdp";
        AdvertiseIceServers(context);
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

/// <summary>The sessions WHIP and WHEP endpoints made, by resource id; ends them all at shutdown.</summary>
internal sealed partial class HttpMediaResources(ILogger<HttpMediaResources>? logger = null)
    : IAsyncDisposable
{
    private static readonly TimeSpan AnswerLimit = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, Resource> _resources = new(
        StringComparer.Ordinal
    );
    private readonly ILogger _logger = logger ?? NullLogger<HttpMediaResources>.Instance;

    public int Count => _resources.Count;

    // Makes the answering session and returns its answer, or null when it could not answer.
    public Task<string?> AnswerAsync(
        IMediaSessionFactory sessions,
        HttpMediaSetup setup,
        string offer,
        string protocol,
        out string id,
        CancellationToken cancellationToken
    )
    {
        id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        return AnswerCoreAsync(sessions, setup, offer, protocol, id, cancellationToken);
    }

    private async Task<string?> AnswerCoreAsync(
        IMediaSessionFactory sessions,
        HttpMediaSetup setup,
        string offer,
        string protocol,
        string id,
        CancellationToken cancellationToken
    )
    {
        OfferChannel channel = new();
        IMediaSession session = sessions.Create(
            channel,
            MediaSessionRole.Answerer,
            setup.Endpoints,
            setup.Options
        );
        Resource resource = new(session, channel, offer, id, setup.Ended);
        try
        {
            await session.StartAsync(cancellationToken).ConfigureAwait(false);
            string answer = await channel
                .AnswerAsync(offer, AnswerLimit, cancellationToken)
                .ConfigureAwait(false);
            _resources[id] = resource;
            session.StateChanged += state =>
            {
                if (state is TransportState.Failed or TransportState.Closed)
                {
                    _ = EndAsync(id);
                }
            };
            LogCreated(protocol, id);
            return answer;
        }
        catch (Exception exception)
            when (exception
                    is TimeoutException
                        or InvalidOperationException
                        or OperationCanceledException
            )
        {
            LogAnswerFailed(exception, protocol);
            await resource.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    // A client's PATCH (RFC 9725 section 4.3): trickled candidates for its ICE session, or an ICE restart.
    // The status to answer, and for a restart the new session's tag and the server's fragment.
    public async Task<(int Status, string? Tag, string? Fragment)> TrickleAsync(
        string id,
        StringValues ifMatch,
        string body,
        CancellationToken cancellationToken
    )
    {
        if (!_resources.TryGetValue(id, out Resource? resource))
        {
            return (StatusCodes.Status404NotFound, null, null);
        }

        await resource.Patching.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The resource's ETag names its ICE session; a PATCH must say which one it means.
            if (StringValues.IsNullOrEmpty(ifMatch))
            {
                return (StatusCodes.Status428PreconditionRequired, null, null);
            }

            if (
                !ifMatch.Any(value =>
                    value is not null
                    && value
                        .Split(',')
                        .Any(tag => tag.Trim() is "*" || tag.Trim() == $"\"{resource.Tag}\"")
                )
            )
            {
                return (StatusCodes.Status412PreconditionFailed, null, null);
            }

            if (!TrickleIceFragment.TryParse(body, out TrickleIceFragment fragment))
            {
                return (StatusCodes.Status400BadRequest, null, null);
            }

            if (
                fragment.UsernameFragment is { } ufrag
                && !string.Equals(ufrag, resource.RemoteUsernameFragment, StringComparison.Ordinal)
            )
            {
                return await RestartAsync(id, resource, fragment, cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (string candidate in fragment.Candidates)
            {
                await resource.Channel.DeliverAsync(candidate, fragment.Mid).ConfigureAwait(false);
            }

            LogTrickled(id, fragment.Candidates.Count);
            return (StatusCodes.Status204NoContent, null, null);
        }
        finally
        {
            _ = resource.Patching.Release();
        }
    }

    // New credentials are an ICE restart (RFC 9725 section 4.3.3): the client's fragment stands in for a
    // new offer, the rest of the first one still applying; the session restarts ICE and answers, and the
    // answer's credentials and candidates go back with a new tag for the new ICE session. A restart that
    // fails leaves the session and its ICE session as they were.
    private async Task<(int Status, string? Tag, string? Fragment)> RestartAsync(
        string id,
        Resource resource,
        TrickleIceFragment fragment,
        CancellationToken cancellationToken
    )
    {
        string offer = fragment.ApplyTo(resource.Offer);
        string answer;
        try
        {
            answer = await resource
                .Channel.RestartAsync(offer, AnswerLimit, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            LogRestartFailed(exception, id);
            return (StatusCodes.Status503ServiceUnavailable, null, null);
        }

        resource.Offer = offer;
        resource.Tag = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var ours = TrickleIceFragment.FromFirstSection(answer);
        LogRestarted(id, fragment.Candidates.Count, ours.Candidates.Count);
        return (StatusCodes.Status200OK, resource.Tag, ours.Format());
    }

    public async Task<bool> EndAsync(string id)
    {
        if (!_resources.TryRemove(id, out Resource? resource))
        {
            return false;
        }

        await resource.DisposeAsync().ConfigureAwait(false);
        LogEnded(id);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (string id in _resources.Keys)
        {
            _ = await EndAsync(id).ConfigureAwait(false);
        }
    }

    [LoggerMessage(2710, LogLevel.Information, "{Protocol} session {Resource} answered.")]
    private partial void LogCreated(string protocol, string resource);

    [LoggerMessage(2711, LogLevel.Information, "Session {Resource} ended.")]
    private partial void LogEnded(string resource);

    [LoggerMessage(2712, LogLevel.Warning, "A {Protocol} offer could not be answered.")]
    private partial void LogAnswerFailed(Exception exception, string protocol);

    [LoggerMessage(2713, LogLevel.Debug, "Session {Resource} took {Count} trickled candidates.")]
    private partial void LogTrickled(string resource, int count);

    [LoggerMessage(
        2714,
        LogLevel.Information,
        "Session {Resource} restarted ICE: the client gave {Remote} candidates, the server {Local}."
    )]
    private partial void LogRestarted(string resource, int remote, int local);

    [LoggerMessage(
        2716,
        LogLevel.Warning,
        "Session {Resource}: an ICE restart was not answered in time; the ICE session stays as it was."
    )]
    private partial void LogRestartFailed(Exception exception, string resource);

    private sealed class Resource(
        IMediaSession session,
        OfferChannel channel,
        string offer,
        string tag,
        Func<ValueTask>? ended
    ) : IAsyncDisposable
    {
        private int _disposed;

        public OfferChannel Channel { get; } = channel;

        // One PATCH at a time, so a restart and the trickles around it apply in the order they arrive.
        public SemaphoreSlim Patching { get; } = new(1, 1);

        // The client's offer as it stands, ICE restarts applied: a PATCH with other credentials restarts.
        public string Offer { get; set; } = offer;

        public string? RemoteUsernameFragment =>
            TrickleIceFragment.ForFirstSection(Offer, []).UsernameFragment;

        // The entity tag of the current ICE session.
        public string Tag { get; set; } = tag;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await session.DisposeAsync().ConfigureAwait(false);
            Patching.Dispose();
            if (ended is not null)
            {
                await ended().ConfigureAwait(false);
            }
        }
    }

    // The server's side of WHIP and WHEP signaling: the offer in, the answer out, candidates inside both.
    private sealed class OfferChannel : ISignalingChannel
    {
        private readonly TaskCompletionSource<string> _answer = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        // The answer to an ICE restart's offer, while one is out.
        private TaskCompletionSource<string>? _restart;

        // Hands the session an ICE restart's offer and waits for its answer.
        public async Task<string> RestartAsync(
            string offer,
            TimeSpan limit,
            CancellationToken cancellationToken
        )
        {
            TaskCompletionSource<string> answered = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _restart = answered;
            try
            {
                if (DescriptionReceived is { } received)
                {
                    await received(new SessionDescription(SdpKind.Offer, offer))
                        .ConfigureAwait(false);
                }

                return await answered
                    .Task.WaitAsync(limit, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _restart = null;
            }
        }

        public event Func<SessionDescription, Task>? DescriptionReceived;

        public event Func<IceCandidateInit, Task>? IceCandidateReceived;

        public bool SupportsTrickle => false;

        // A candidate the client trickled after its offer.
        public async Task DeliverAsync(string candidate, string? mid)
        {
            if (IceCandidateReceived is { } received)
            {
                await received(new IceCandidateInit(candidate, mid, 0)).ConfigureAwait(false);
            }
        }

        public async Task<string> AnswerAsync(
            string offer,
            TimeSpan limit,
            CancellationToken cancellationToken
        )
        {
            if (DescriptionReceived is { } received)
            {
                await received(new SessionDescription(SdpKind.Offer, offer)).ConfigureAwait(false);
            }

            return await _answer.Task.WaitAsync(limit, cancellationToken).ConfigureAwait(false);
        }

        public Task SendAsync(
            SessionDescription description,
            CancellationToken cancellationToken = default
        )
        {
            if (description.Kind == SdpKind.Answer)
            {
                _ = _restart is { } restart
                    ? restart.TrySetResult(description.Sdp)
                    : _answer.TrySetResult(description.Sdp);
            }

            return Task.CompletedTask;
        }

        public Task SendAsync(
            IceCandidateInit candidate,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
