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

            // RFC 8840 section 4.4: an empty candidate tells the session no more follow.
            if (fragment.EndOfCandidates)
            {
                await resource
                    .Channel.DeliverAsync(string.Empty, fragment.Mid)
                    .ConfigureAwait(false);
            }

            LogTrickled(id, fragment.Candidates.Length);
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
        LogRestarted(id, fragment.Candidates.Length, ours.Candidates.Length);
        return (StatusCodes.Status200OK, resource.Tag, ours.Format());
    }

    /// <summary>Whether a session resource exists.</summary>
    /// <param name="id">The resource id.</param>
    /// <returns>Whether it exists.</returns>
    public bool Exists(string id) => _resources.ContainsKey(id);

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

        public Task SendDescriptionAsync(
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

        public Task SendCandidateAsync(
            IceCandidateInit candidate,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
