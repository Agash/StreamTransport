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
/// with them. Candidates travel inside the offer and answer; trickle (<c>PATCH</c>) is answered
/// <c>405</c>. The returned group takes the host's conventions, such as <c>RequireAuthorization</c> or
/// <c>RequireCors</c>.
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
        group.MapMethods("/{resource}", [HttpMethods.Patch], (RequestDelegate)NoTrickle);
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

    private static Task NoTrickle(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        context.Response.Headers.Allow = "DELETE";
        return Task.CompletedTask;
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
        Resource resource = new(session, setup.Ended);
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

    private sealed class Resource(IMediaSession session, Func<ValueTask>? ended) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await session.DisposeAsync().ConfigureAwait(false);
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

        public event Func<SessionDescription, Task>? DescriptionReceived;

        public event Func<IceCandidateInit, Task>? IceCandidateReceived;

        public bool SupportsTrickle => false;

        public async Task<string> AnswerAsync(
            string offer,
            TimeSpan limit,
            CancellationToken cancellationToken
        )
        {
            _ = IceCandidateReceived;
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
                _ = _answer.TrySetResult(description.Sdp);
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
