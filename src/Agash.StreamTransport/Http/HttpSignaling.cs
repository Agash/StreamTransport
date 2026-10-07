using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.Http;

/// <summary>How a WHIP or WHEP client reaches its server.</summary>
public sealed record HttpSignalingOptions
{
    /// <summary>The bearer token the server asks for, if any (RFC 9725 section 4.5).</summary>
    public string? BearerToken { get; init; }

    /// <summary>The client to send requests with; a new one when null.</summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>How long the server may take to answer.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// A session signaled over HTTP: WHIP to publish (RFC 9725), WHEP to play. The client posts its offer,
/// with every candidate in it, and the server answers once. Candidates gathered later, such as on an
/// interface that came up mid-session, go to the resource in <c>PATCH</c> requests (RFC 9725 section 4.3.2),
/// so a server behind a firewall checks the new address and lets it in. Disposing the session deletes the
/// resource the server made for it.
/// </summary>
public sealed class HttpMediaSession : IAsyncDisposable
{
    private readonly HttpSdpChannel _channel;

    internal HttpMediaSession(IMediaSession session, HttpSdpChannel channel)
    {
        Session = session;
        _channel = channel;
    }

    /// <summary>The media session.</summary>
    public IMediaSession Session { get; }

    /// <summary>The server's resource for this session, once it answered.</summary>
    public Uri? Resource => _channel.Resource;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync().ConfigureAwait(false);
        await _channel.DisposeAsync().ConfigureAwait(false);
    }

    // Starts a session as the offerer over a channel that posts its offer to the endpoint.
    internal static async Task<HttpMediaSession> StartAsync(
        IMediaSessionFactory sessions,
        Uri endpoint,
        MediaEndpoints endpoints,
        MediaSessionOptions options,
        HttpSignalingOptions? signaling,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);
        HttpSdpChannel channel = new(endpoint, signaling ?? new HttpSignalingOptions(), logger);
        if (options.Transport.IceServers.IsEmpty)
        {
            // The server's own STUN and TURN servers, when it advertises them.
            options = options with
            {
                Transport = options.Transport with
                {
                    IceServers =
                    [
                        .. await channel
                            .DiscoverIceServersAsync(cancellationToken)
                            .ConfigureAwait(false),
                    ],
                },
            };
        }

        IMediaSession session = sessions.Create(
            channel,
            MediaSessionRole.Offerer,
            endpoints,
            options
        );
        try
        {
            await session.StartAsync(cancellationToken).ConfigureAwait(false);
            await channel.Answered.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new HttpMediaSession(session, channel);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>Publishes media to a WHIP endpoint (RFC 9725), as OBS, a streaming service or StreamWeaver hosts one.</summary>
public static class WhipClient
{
    /// <summary>Publishes the sources of <paramref name="endpoints"/> to a WHIP endpoint.</summary>
    /// <param name="sessions">Makes the media session.</param>
    /// <param name="endpoint">The WHIP endpoint URL.</param>
    /// <param name="endpoints">The video and audio to send.</param>
    /// <param name="options">How the session is set up.</param>
    /// <param name="signaling">How the server is reached; defaults when null.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">Cancels publishing.</param>
    /// <returns>The session, answered; it connects in the background.</returns>
    /// <exception cref="HttpRequestException">The server refused the offer.</exception>
    public static Task<HttpMediaSession> PublishAsync(
        IMediaSessionFactory sessions,
        Uri endpoint,
        MediaEndpoints endpoints,
        MediaSessionOptions options,
        HttpSignalingOptions? signaling = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default
    ) =>
        HttpMediaSession.StartAsync(
            sessions,
            endpoint,
            endpoints,
            options,
            signaling,
            logger ?? NullLogger.Instance,
            cancellationToken
        );
}

/// <summary>Plays media from a WHEP endpoint, as a streaming service or StreamWeaver hosts one.</summary>
public static class WhepClient
{
    /// <summary>Plays a WHEP endpoint into the sinks of <paramref name="endpoints"/>.</summary>
    /// <param name="sessions">Makes the media session.</param>
    /// <param name="endpoint">The WHEP endpoint URL.</param>
    /// <param name="endpoints">Where the received video and audio go.</param>
    /// <param name="options">How the session is set up.</param>
    /// <param name="signaling">How the server is reached; defaults when null.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">Cancels playing.</param>
    /// <returns>The session, answered; it connects in the background.</returns>
    /// <exception cref="HttpRequestException">The server refused the offer.</exception>
    public static Task<HttpMediaSession> PlayAsync(
        IMediaSessionFactory sessions,
        Uri endpoint,
        MediaEndpoints endpoints,
        MediaSessionOptions options,
        HttpSignalingOptions? signaling = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default
    ) =>
        HttpMediaSession.StartAsync(
            sessions,
            endpoint,
            endpoints,
            options,
            signaling,
            logger ?? NullLogger.Instance,
            cancellationToken
        );
}

// The client's side of WHIP and WHEP signaling: one offer out, one answer back, candidates inside both,
// and candidates gathered afterwards trickled in PATCH requests.
internal sealed partial class HttpSdpChannel(
    Uri endpoint,
    HttpSignalingOptions options,
    ILogger logger
) : ISignalingChannel
{
    private const string SdpMediaType = "application/sdp";

    private readonly HttpClient _http = options.HttpClient ?? new HttpClient();
    private readonly bool _ownsClient = options.HttpClient is null;
    private readonly TaskCompletionSource _answered = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    // Candidates waiting to be trickled: until the answer names the resource, and while a PATCH is out.
    // One PATCH carries everything waiting (RFC 9725 section 4.3.2).
    private readonly List<string> _pending = [];
    private readonly SemaphoreSlim _trickling = new(1, 1);
    private string? _offer;
    private EntityTagHeaderValue? _iceSession;
    private bool _trickleRefused;

    public event Func<SessionDescription, Task>? DescriptionReceived;

    public event Func<IceCandidateInit, Task>? IceCandidateReceived;

    public bool SupportsTrickle => false;

    public Uri? Resource { get; private set; }

    public Task Answered => _answered.Task;

    public async Task SendAsync(
        SessionDescription description,
        CancellationToken cancellationToken = default
    )
    {
        if (description.Kind != SdpKind.Offer)
        {
            throw new InvalidOperationException("A WHIP or WHEP client only offers.");
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(description.Sdp, Encoding.UTF8, SdpMediaType),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(SdpMediaType);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(SdpMediaType));
            Authorize(request);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);
            using HttpResponseMessage response = await _http
                .SendAsync(request, timeout.Token)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Created)
            {
                throw new HttpRequestException(
                    $"The server answered the offer with {(int)response.StatusCode} {response.ReasonPhrase}.",
                    null,
                    response.StatusCode
                );
            }

            Resource = response.Headers.Location is { } location
                ? new Uri(endpoint, location)
                : null;
            _offer = description.Sdp;
            _iceSession = response.Headers.ETag;
            string answer = await response
                .Content.ReadAsStringAsync(timeout.Token)
                .ConfigureAwait(false);
            LogAnswered(endpoint, Resource);
            if (DescriptionReceived is { } received)
            {
                await received(new SessionDescription(SdpKind.Answer, answer))
                    .ConfigureAwait(false);
            }

            _answered.TrySetResult();
        }
        catch (Exception exception)
        {
            _answered.TrySetException(exception);
            throw;
        }

        await TrickleAsync(cancellationToken).ConfigureAwait(false);
    }

    // OPTIONS on the endpoint returns the ICE servers the server advertises (RFC 9725 section 4.6); a
    // server that answers nothing useful leaves the session with none.
    public async Task<List<IceServer>> DiscoverIceServersAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, endpoint);
            Authorize(request);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);
            using HttpResponseMessage response = await _http
                .SendAsync(request, timeout.Token)
                .ConfigureAwait(false);
            List<IceServer> servers = response.Headers.TryGetValues(
                "Link",
                out IEnumerable<string>? links
            )
                ? IceServerLinks.Parse(links)
                : [];
            LogDiscovered(endpoint, servers.Count);
            return servers;
        }
        catch (HttpRequestException exception)
        {
            LogDiscoveryFailed(exception, endpoint);
            return [];
        }
    }

    // A candidate gathered after the offer: trickled once the resource exists, with any others waiting.
    public async Task SendAsync(
        IceCandidateInit candidate,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrEmpty(candidate.Candidate))
        {
            return;
        }

        lock (_pending)
        {
            if (_trickleRefused)
            {
                return;
            }

            _pending.Add(
                candidate.Candidate.StartsWith("a=", StringComparison.Ordinal)
                    ? candidate.Candidate[2..]
                    : candidate.Candidate
            );
        }

        await TrickleAsync(cancellationToken).ConfigureAwait(false);
    }

    // Sends what is waiting in one PATCH. One at a time, so the candidates arrive in the order gathered;
    // a candidate added while one is out goes in the next.
    private async Task TrickleAsync(CancellationToken cancellationToken)
    {
        if (Resource is not { } resource || _offer is not { } offer)
        {
            return;
        }

        await _trickling.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string[] candidates;
            lock (_pending)
            {
                if (_trickleRefused || _pending.Count == 0)
                {
                    return;
                }

                candidates = [.. _pending];
                _pending.Clear();
            }

            string body = TrickleIceFragment.ForFirstSection(offer, candidates).Format();
            using HttpRequestMessage request = new(HttpMethod.Patch, resource)
            {
                Content = new StringContent(body, Encoding.UTF8, TrickleIceFragment.MediaType),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(
                TrickleIceFragment.MediaType
            );
            if (_iceSession is { } session)
            {
                request.Headers.IfMatch.Add(session);
            }

            Authorize(request);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);
            using HttpResponseMessage response = await _http
                .SendAsync(request, timeout.Token)
                .ConfigureAwait(false);
            switch (response.StatusCode)
            {
                case HttpStatusCode.NoContent or HttpStatusCode.OK:
                    LogTrickled(resource, candidates.Length);
                    break;
                case HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.UnprocessableContent
                or HttpStatusCode.NotImplemented
                or HttpStatusCode.UnsupportedMediaType:
                    // The server takes no trickled candidates: the ones in the offer are all it gets.
                    lock (_pending)
                    {
                        _trickleRefused = true;
                        _pending.Clear();
                    }

                    LogTrickleRefused(resource, (int)response.StatusCode);
                    break;
                default:
                    LogTrickleFailed(resource, (int)response.StatusCode, candidates.Length);
                    break;
            }
        }
        catch (HttpRequestException exception)
        {
            // Candidates gathered later go in the next PATCH; the session carries on without these.
            LogTrickleError(exception, resource);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogTrickleTimedOut(resource);
        }
        finally
        {
            _ = _trickling.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _ = IceCandidateReceived;
        if (Resource is { } resource)
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Delete, resource);
                Authorize(request);
                using HttpResponseMessage response = await _http
                    .SendAsync(request)
                    .ConfigureAwait(false);
                LogDeleted(resource, (int)response.StatusCode);
            }
            catch (HttpRequestException exception)
            {
                LogDeleteFailed(exception, resource);
            }
        }

        if (_ownsClient)
        {
            _http.Dispose();
        }

        _trickling.Dispose();
    }

    private void Authorize(HttpRequestMessage request)
    {
        if (options.BearerToken is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    [LoggerMessage(
        2700,
        LogLevel.Information,
        "{Endpoint} answered; the session's resource is {Resource}."
    )]
    private partial void LogAnswered(Uri endpoint, Uri? resource);

    [LoggerMessage(2701, LogLevel.Debug, "Deleted {Resource}: {Status}.")]
    private partial void LogDeleted(Uri resource, int status);

    [LoggerMessage(
        2702,
        LogLevel.Warning,
        "Deleting {Resource} failed; the server ends it when it times out."
    )]
    private partial void LogDeleteFailed(Exception exception, Uri resource);

    [LoggerMessage(2705, LogLevel.Debug, "Trickled {Count} candidates to {Resource}.")]
    private partial void LogTrickled(Uri resource, int count);

    [LoggerMessage(
        2706,
        LogLevel.Information,
        "{Resource} takes no trickled candidates ({Status}); later candidates are not sent."
    )]
    private partial void LogTrickleRefused(Uri resource, int status);

    [LoggerMessage(
        2707,
        LogLevel.Warning,
        "Trickling {Count} candidates to {Resource} failed with {Status}."
    )]
    private partial void LogTrickleFailed(Uri resource, int status, int count);

    [LoggerMessage(2708, LogLevel.Warning, "Trickling candidates to {Resource} failed.")]
    private partial void LogTrickleError(Exception exception, Uri resource);

    [LoggerMessage(2709, LogLevel.Warning, "Trickling candidates to {Resource} timed out.")]
    private partial void LogTrickleTimedOut(Uri resource);

    [LoggerMessage(2703, LogLevel.Debug, "{Endpoint} advertises {Count} ICE servers.")]
    private partial void LogDiscovered(Uri endpoint, int count);

    [LoggerMessage(
        2704,
        LogLevel.Debug,
        "Asking {Endpoint} for its ICE servers failed; gathering without them."
    )]
    private partial void LogDiscoveryFailed(Exception exception, Uri endpoint);
}
