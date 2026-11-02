using System.Collections.Immutable;
using System.Globalization;
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

    /// <summary>
    /// The client to send requests with; a new one when null, which follows redirects itself so the bearer
    /// token goes to the server a redirect names. A client of your own should not follow them automatically:
    /// .NET drops the Authorization header on a redirect to another host.
    /// </summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>How long the server may take to answer.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The clock the timeouts run on; the system's when null.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>
    /// Lets the endpoint, the URLs a redirect names and the session's resource use plain <c>http</c> on a
    /// host other than this machine's loopback. RFC 9725 section 7 requires HTTPS: the exchange carries the
    /// ICE password, the bearer token and the server's TURN credentials. For a test server on a trusted
    /// network only.
    /// </summary>
    public bool AllowInsecureHttp { get; init; }
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
        signaling ??= new HttpSignalingOptions();
        HttpSdpChannel.RequireSecure(endpoint, signaling);
        HttpSdpChannel channel = new(endpoint, signaling, sessions.DescriptionFormat, logger);
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

/// <summary>Publishes media to a WHIP endpoint (RFC 9725), as OBS or a streaming service hosts one.</summary>
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

/// <summary>Plays media from a WHEP endpoint, as a streaming service hosts one.</summary>
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
    ISessionDescriptionFormat descriptions,
    ILogger logger
) : ISignalingChannel
{
    private const string SdpMediaType = "application/sdp";

    // Redirects are followed here: the handler's own following drops the bearer token across hosts.
    private readonly HttpClient _http =
        options.HttpClient ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
    private readonly TimeProvider _time = options.TimeProvider ?? TimeProvider.System;
    private const int MaxRedirects = 5;
    private static readonly TimeSpan CounterOfferLifetime = TimeSpan.FromSeconds(30);

    // Where the offer goes: the endpoint, or where a redirect moved it (RFC 9725 section 4.5).
    private Uri _endpoint = endpoint;
    private readonly bool _ownsClient = options.HttpClient is null;
    private readonly TaskCompletionSource _answered = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    // Candidates waiting to be trickled: until the answer names the resource, and while a PATCH is out.
    // One PATCH carries everything waiting (RFC 9725 section 4.3.2).
    private readonly List<string> _pending = [];
    private readonly SemaphoreSlim _trickling = new(1, 1);

    // This side's description and the server's: the offer and its answer, or after a WHEP counter-offer
    // the answer and the server's offer.
    private string? _local;
    private string? _remote;
    private EntityTagHeaderValue? _iceSession;

    // WHEP section 4.3.2: until when the server's counter-offer waits for this side's answer.
    private DateTimeOffset? _counterOfferValidUntil;
    private bool _trickleRefused;

    public event Func<SessionDescription, Task>? DescriptionReceived;

    public event Func<IceCandidateInit, Task>? IceCandidateReceived;

    public bool SupportsTrickle => false;

    public Uri? Resource { get; private set; }

    public Task Answered => _answered.Task;

    public async Task SendDescriptionAsync(
        SessionDescription description,
        CancellationToken cancellationToken = default
    )
    {
        if (description.Kind == SdpKind.Answer)
        {
            await AnswerCounterOfferAsync(description, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Resource is { } existing && _remote is not null)
        {
            await RestartAsync(existing, description, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            HttpRequestMessage Offer()
            {
                HttpRequestMessage request = new(HttpMethod.Post, _endpoint)
                {
                    Content = new StringContent(description.Sdp, Encoding.UTF8, SdpMediaType),
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(SdpMediaType);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(SdpMediaType));
                Authorize(request);
                return request;
            }

            using CancellationTokenSource deadline = new(options.Timeout, _time);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token
            );
            HttpResponseMessage response = await SendFollowingRedirectsAsync(Offer, timeout.Token)
                .ConfigureAwait(false);
            using HttpResponseMessage answered = response;
            if (
                await ReadCounterOfferAsync(response, timeout.Token).ConfigureAwait(false) is
                { } counterOffer
            )
            {
                LogCounterOffered(_endpoint, Resource);
                if (DescriptionReceived is { } offered)
                {
                    await offered(new SessionDescription(SdpKind.Offer, counterOffer))
                        .ConfigureAwait(false);
                }

                return;
            }

            if (response.StatusCode != HttpStatusCode.Created)
            {
                throw new HttpRequestException(
                    $"The server answered the offer with {(int)response.StatusCode} {response.ReasonPhrase}.",
                    null,
                    response.StatusCode
                );
            }

            Resource = response.Headers.Location is { } location
                ? RequireSecure(new Uri(_endpoint, location), options)
                : null;
            _local = description.Sdp;
            _iceSession = response.Headers.ETag;
            string answer = await response
                .Content.ReadAsStringAsync(timeout.Token)
                .ConfigureAwait(false);
            _remote = answer;
            LogAnswered(_endpoint, Resource);
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

    // WHEP section 4.3.3: a 406 is a counter-offer only with an application/sdp body that is a
    // syntactically valid offer; any other 406 is an ordinary refusal. Section 4.3.2: the Location names
    // the resource the exchange makes, and the offer waits for the answer until valid-until, an HTTP-date,
    // or 30 seconds from the response without one.
    private async Task<string?> ReadCounterOfferAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        DateTimeOffset received = _time.GetUtcNow();
        if (
            response.StatusCode != HttpStatusCode.NotAcceptable
            || response.Content.Headers.ContentType is not { } type
            || !string.Equals(type.MediaType, SdpMediaType, StringComparison.OrdinalIgnoreCase)
        )
        {
            return null;
        }

        string offer = await response
            .Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!descriptions.IsValid(new SessionDescription(SdpKind.Offer, offer)))
        {
            return null;
        }

        NameValueHeaderValue? validUntil = type.Parameters.FirstOrDefault(static p =>
            p.Name.Equals("valid-until", StringComparison.OrdinalIgnoreCase)
        );
        DateTimeOffset until = received + CounterOfferLifetime;
        if (validUntil is not null && !TryParseHttpDate(validUntil.Value, out until))
        {
            throw new HttpRequestException(
                $"The server's counter-offer has valid-until {validUntil.Value}, which is not an HTTP-date (WHEP section 4.3.2).",
                null,
                response.StatusCode
            );
        }

        Resource = response.Headers.Location is { } location
            ? RequireSecure(new Uri(_endpoint, location), options)
            : throw new HttpRequestException(
                "The server's counter-offer names no resource to answer it at (WHEP section 4.3.2).",
                null,
                response.StatusCode
            );
        _remote = offer;
        _iceSession = response.Headers.ETag;
        _counterOfferValidUntil = until;
        return offer;
    }

    // RFC 9110 section 5.6.7: recipients take IMF-fixdate and the two obsolete forms, all in GMT.
    private static readonly string[] HttpDateFormats =
    [
        "r",
        "dddd, dd-MMM-yy HH:mm:ss 'GMT'",
        "ddd MMM d HH:mm:ss yyyy",
    ];

    private static bool TryParseHttpDate(string? value, out DateTimeOffset date) =>
        DateTimeOffset.TryParseExact(
            value?.Trim('"'),
            HttpDateFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowInnerWhite,
            out date
        );

    // WHEP section 4.3.2: the answer to a counter-offer goes to the resource in a PATCH, which the server
    // takes with 204.
    private async Task AnswerCounterOfferAsync(
        SessionDescription answer,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (Resource is not { } resource || _counterOfferValidUntil is not { } validUntil)
            {
                throw new InvalidOperationException(
                    "A WHIP or WHEP client answers only a WHEP counter-offer."
                );
            }

            if (_time.GetUtcNow() > validUntil)
            {
                throw new TimeoutException(
                    $"The server's counter-offer expired at {validUntil:R} before it was answered."
                );
            }

            using HttpRequestMessage request = new(HttpMethod.Patch, resource)
            {
                Content = new StringContent(answer.Sdp, Encoding.UTF8, SdpMediaType),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(SdpMediaType);
            Authorize(request);
            using CancellationTokenSource deadline = new(options.Timeout, _time);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token
            );
            using HttpResponseMessage response = await _http
                .SendAsync(request, timeout.Token)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.NoContent)
            {
                throw new HttpRequestException(
                    $"The server answered the counter-offer's answer with {(int)response.StatusCode} {response.ReasonPhrase}.",
                    null,
                    response.StatusCode
                );
            }

            _local = answer.Sdp;
            _counterOfferValidUntil = null;

            // RFC 9725 section 4.3.1: trickled candidates carry the ICE session's ETag in If-Match. WHEP
            // gives the counter-offer path none of its own; the 204 or the 406 may carry one, and without
            // either a later candidate has nothing to match and is not sent.
            _iceSession = response.Headers.ETag ?? _iceSession;
            if (_iceSession is null)
            {
                lock (_pending)
                {
                    _trickleRefused = true;
                    _pending.Clear();
                }

                LogNoIceSessionTag(resource);
            }

            LogCounterOfferAnswered(resource);
            _answered.TrySetResult();
        }
        catch (Exception exception)
        {
            _answered.TrySetException(exception);
            throw;
        }

        await TrickleAsync(cancellationToken).ConfigureAwait(false);
    }

    // RFC 9725 section 7: HTTPS, since the requests carry the ICE password and the bearer token and the
    // answers TURN credentials. Plain http only to this machine's loopback, or when the application allows
    // it; a server cannot downgrade the client by redirecting it or by naming an http resource.
    internal static Uri RequireSecure(Uri uri, HttpSignalingOptions options)
    {
        if (
            uri.Scheme == Uri.UriSchemeHttps
            || (uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || options.AllowInsecureHttp))
        )
        {
            return uri;
        }

        throw new HttpRequestException(
            $"WHIP and WHEP go over HTTPS (RFC 9725 section 7); {uri.GetLeftPart(UriPartial.Authority)} is not."
        );
    }

    // RFC 9725 section 4.5: a 307 or 308 moves the offer to another URL, the bearer token with it, and later
    // requests go there too.
    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
        Func<HttpRequestMessage> request,
        CancellationToken cancellationToken
    )
    {
        for (int redirects = 0; ; redirects++)
        {
            using HttpRequestMessage message = request();
            HttpResponseMessage response = await _http
                .SendAsync(message, cancellationToken)
                .ConfigureAwait(false);
            if (
                response.StatusCode
                    is not (HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                || response.Headers.Location is not { } location
                || redirects == MaxRedirects
            )
            {
                return response;
            }

            response.Dispose();
            _endpoint = RequireSecure(new Uri(_endpoint, location), options);
            LogRedirected(_endpoint);
        }
    }

    // OPTIONS on the endpoint returns the ICE servers the server advertises (RFC 9725 section 4.6); a
    // server that answers nothing useful leaves the session with none.
    public async Task<ImmutableArray<IceServer>> DiscoverIceServersAsync(
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, _endpoint);
            Authorize(request);
            using CancellationTokenSource deadline = new(options.Timeout, _time);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token
            );
            using HttpResponseMessage response = await _http
                .SendAsync(request, timeout.Token)
                .ConfigureAwait(false);
            ImmutableArray<IceServer> servers = response.Headers.TryGetValues(
                "Link",
                out IEnumerable<string>? links
            )
                ? IceServerLinks.Parse(links)
                : [];
            LogDiscovered(_endpoint, servers.Length);
            return servers;
        }
        catch (HttpRequestException exception)
        {
            LogDiscoveryFailed(exception, _endpoint);
            return [];
        }
    }

    // A candidate gathered after the offer: trickled once the resource exists, with any others waiting.
    public async Task SendCandidateAsync(
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

    // An ICE restart (RFC 9725 section 4.3.3): the new credentials and candidates in a PATCH with
    // If-Match "*"; the server's come back in a 200 with the new ICE session's tag, and stand in for an
    // answer to the restart offer, the rest of the first answer still applying. Trickles wait behind it,
    // so they carry the new session's credentials and tag.
    private async Task RestartAsync(
        Uri resource,
        SessionDescription offer,
        CancellationToken cancellationToken
    )
    {
        string answer;
        await _trickling.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_pending)
            {
                // Candidates of the old ICE session mean nothing to the new one.
                _pending.Clear();
            }

            using HttpRequestMessage request = new(HttpMethod.Patch, resource)
            {
                Content = new StringContent(
                    TrickleIceFragment.FromFirstSection(offer.Sdp).Format(),
                    Encoding.UTF8,
                    TrickleIceFragment.MediaType
                ),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(
                TrickleIceFragment.MediaType
            );
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Any);
            Authorize(request);
            using CancellationTokenSource deadline = new(options.Timeout, _time);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token
            );
            using HttpResponseMessage response = await _http
                .SendAsync(request, timeout.Token)
                .ConfigureAwait(false);
            string body = await response
                .Content.ReadAsStringAsync(timeout.Token)
                .ConfigureAwait(false);
            if (
                response.StatusCode != HttpStatusCode.OK
                || !TrickleIceFragment.TryParse(body, out TrickleIceFragment restarted)
            )
            {
                // The server keeps the ICE session it has (RFC 9725 section 4.3.3).
                throw new HttpRequestException(
                    $"The server answered the ICE restart with {(int)response.StatusCode} {response.ReasonPhrase}.",
                    null,
                    response.StatusCode
                );
            }

            _local = offer.Sdp;
            _iceSession = response.Headers.ETag;
            answer = restarted.ApplyTo(_remote!);
            _remote = answer;
            LogRestarted(resource, restarted.Candidates.Length);
        }
        finally
        {
            _ = _trickling.Release();
        }

        if (DescriptionReceived is { } received)
        {
            await received(new SessionDescription(SdpKind.Answer, answer)).ConfigureAwait(false);
        }

        await TrickleAsync(cancellationToken).ConfigureAwait(false);
    }

    // Sends what is waiting in one PATCH. One at a time, so the candidates arrive in the order gathered;
    // a candidate added while one is out goes in the next.
    private async Task TrickleAsync(CancellationToken cancellationToken)
    {
        if (Resource is not { } resource || _local is not { } local)
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

            string body = TrickleIceFragment.ForFirstSection(local, [.. candidates]).Format();
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
            using CancellationTokenSource deadline = new(options.Timeout, _time);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token
            );
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

    [LoggerMessage(
        2715,
        LogLevel.Information,
        "{Resource} restarted ICE; it gave {Count} candidates."
    )]
    private partial void LogRestarted(Uri resource, int count);

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

    [LoggerMessage(2717, LogLevel.Information, "The server moved the session to {Endpoint}.")]
    private partial void LogRedirected(Uri endpoint);

    [LoggerMessage(
        2718,
        LogLevel.Information,
        "{Endpoint} refused the offer with a counter-offer; it is answered at {Resource}."
    )]
    private partial void LogCounterOffered(Uri endpoint, Uri? resource);

    [LoggerMessage(2719, LogLevel.Information, "{Resource} took the answer to its counter-offer.")]
    private partial void LogCounterOfferAnswered(Uri resource);

    [LoggerMessage(
        2720,
        LogLevel.Information,
        "{Resource} gave the counter-offered session no ETag; candidates gathered later are not trickled."
    )]
    private partial void LogNoIceSessionTag(Uri resource);
}
