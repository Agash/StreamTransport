using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport;

/// <summary>A session the publisher serves, with the peer it serves.</summary>
/// <param name="Peer">The subscriber.</param>
/// <param name="Session">The session.</param>
public readonly record struct PublishedSession(PeerId Peer, IMediaSession Session);

/// <summary>
/// Publishes sources to every subscriber in a room: each subscriber that joins gets its own session,
/// offered by the publisher, and loses it when it leaves. The sources are shared: each session connects
/// to them with its own encoder.
/// </summary>
public sealed partial class MediaPublisher : IAsyncDisposable
{
    private readonly IMediaRoom _room;
    private readonly IMediaSessionFactory _sessions;
    private readonly MediaEndpoints _sources;
    private readonly MediaSessionOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<PeerId, IMediaSession> _served = new();
    private readonly CancellationTokenSource _stop = new();
    private bool _started;

    /// <summary>A publisher of sources over a room joined as publisher.</summary>
    /// <param name="room">The room.</param>
    /// <param name="sessions">Makes the sessions.</param>
    /// <param name="sources">What to send; sinks are ignored.</param>
    /// <param name="options">How each session is set up; the room's ICE servers fill in when it has none.</param>
    /// <param name="logger">The logger.</param>
    public MediaPublisher(
        IMediaRoom room,
        IMediaSessionFactory sessions,
        MediaEndpoints sources,
        MediaSessionOptions options,
        ILogger<MediaPublisher>? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        if (sources.VideoSource is null && sources.AudioSource is null)
        {
            throw new ArgumentException(
                "A publisher needs a video or audio source.",
                nameof(sources)
            );
        }

        _room = room;
        _sessions = sessions;
        _sources = new MediaEndpoints
        {
            VideoSource = sources.VideoSource,
            AudioSource = sources.AudioSource,
        };
        _options = options.Transport.IceServers.IsEmpty
            ? options with
            {
                Transport = options.Transport with { IceServers = [.. room.IceServers] },
            }
            : options;
        _logger = logger ?? NullLogger<MediaPublisher>.Instance;
    }

    /// <summary>The sessions being served now.</summary>
    public IReadOnlyList<PublishedSession> Sessions =>
        [.. _served.Select(static entry => new PublishedSession(entry.Key, entry.Value))];

    /// <summary>Serves the subscribers present now and every one that joins later.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _room.PeerJoined += OnPeerJoined;
        _room.PeerLeft += OnPeerLeft;
        foreach (PeerInfo peer in _room.Peers)
        {
            OnPeerJoined(peer);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _room.PeerJoined -= OnPeerJoined;
        _room.PeerLeft -= OnPeerLeft;
        await _stop.CancelAsync().ConfigureAwait(false);
        foreach (PeerId peer in _served.Keys)
        {
            if (_served.TryRemove(peer, out IMediaSession? session))
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        _stop.Dispose();
    }

    private void OnPeerJoined(PeerInfo peer)
    {
        if (peer.Role != PeerRole.Subscriber || _served.ContainsKey(peer.PeerId))
        {
            return;
        }

        IMediaSession session = _sessions.Create(
            _room.ChannelFor(peer.PeerId),
            MediaSessionRole.Offerer,
            _sources,
            _options
        );
        if (!_served.TryAdd(peer.PeerId, session))
        {
            _ = session.DisposeAsync();
            return;
        }

        LogServing(peer.PeerId);
        _ = StartAsync(peer.PeerId, session);
    }

    private async Task StartAsync(PeerId peer, IMediaSession session)
    {
        try
        {
            await session.StartAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Deliberately not logged: the publisher is stopping.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogStartFailed(exception, peer);
            await DropAsync(peer).ConfigureAwait(false);
        }
    }

    private void OnPeerLeft(PeerId peer)
    {
        LogPeerLeft(peer);
        _ = DropAsync(peer);
    }

    private async Task DropAsync(PeerId peer)
    {
        if (_served.TryRemove(peer, out IMediaSession? session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    [LoggerMessage(2200, LogLevel.Information, "Serving subscriber {Peer}.")]
    private partial void LogServing(PeerId peer);

    [LoggerMessage(2201, LogLevel.Information, "Subscriber {Peer} left.")]
    private partial void LogPeerLeft(PeerId peer);

    [LoggerMessage(2202, LogLevel.Warning, "The session with subscriber {Peer} failed to start.")]
    private partial void LogStartFailed(Exception exception, PeerId peer);
}
