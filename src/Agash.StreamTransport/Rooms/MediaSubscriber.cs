using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport;

/// <summary>
/// Receives a room's publisher into sinks: when a publisher is in the room, the subscriber answers its
/// offer in a session of its own, and drops the session when the publisher leaves.
/// </summary>
public sealed partial class MediaSubscriber : IAsyncDisposable
{
    private readonly IMediaRoom _room;
    private readonly IMediaSessionFactory _sessions;
    private readonly MediaEndpoints _sinks;
    private readonly MediaSessionOptions _options;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private (PeerId Peer, IMediaSession Session)? _current;
    private bool _started;

    /// <summary>A subscriber into sinks over a room joined as subscriber.</summary>
    /// <param name="room">The room.</param>
    /// <param name="sessions">Makes the session.</param>
    /// <param name="sinks">Where received media goes; sources are ignored.</param>
    /// <param name="options">How the session is set up; the room's ICE servers fill in when it has none.</param>
    /// <param name="logger">The logger.</param>
    public MediaSubscriber(
        IMediaRoom room,
        IMediaSessionFactory sessions,
        MediaEndpoints sinks,
        MediaSessionOptions options,
        ILogger<MediaSubscriber>? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(sinks);
        ArgumentNullException.ThrowIfNull(options);
        if (sinks.VideoSink is null && sinks.AudioSink is null)
        {
            throw new ArgumentException("A subscriber needs a video or audio sink.", nameof(sinks));
        }

        _room = room;
        _sessions = sessions;
        _sinks = new MediaEndpoints { VideoSink = sinks.VideoSink, AudioSink = sinks.AudioSink };
        _options = options.Transport.IceServers.IsEmpty
            ? options with
            {
                Transport = options.Transport with { IceServers = [.. room.IceServers] },
            }
            : options;
        _logger = logger ?? NullLogger<MediaSubscriber>.Instance;
    }

    /// <summary>The session with the publisher, when there is one.</summary>
    public IMediaSession? Session
    {
        get
        {
            lock (_gate)
            {
                return _current?.Session;
            }
        }
    }

    /// <summary>Raised when a session with a publisher starts; its <see cref="IMediaSession.Connected"/> follows.</summary>
    public event Action<IMediaSession>? SessionStarted;

    /// <summary>Receives the publisher present now, or the next one that joins.</summary>
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

    /// <summary>
    /// How much later received audio plays than its synced slot, as <see cref="IMediaSession.AudioOutputOffset"/>;
    /// applied to the current session and every later one.
    /// </summary>
    public TimeSpan AudioOutputOffset
    {
        get;
        set
        {
            field = value;
            if (Session is { } session)
            {
                session.AudioOutputOffset = value;
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _room.PeerJoined -= OnPeerJoined;
        _room.PeerLeft -= OnPeerLeft;
        await _stop.CancelAsync().ConfigureAwait(false);
        IMediaSession? session;
        lock (_gate)
        {
            session = _current?.Session;
            _current = null;
        }

        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private void OnPeerJoined(PeerInfo peer)
    {
        if (peer.Role != PeerRole.Publisher)
        {
            return;
        }

        IMediaSession session;
        lock (_gate)
        {
            if (_current is not null)
            {
                return;
            }

            session = _sessions.Create(
                _room.ChannelFor(peer.PeerId),
                MediaSessionRole.Answerer,
                _sinks,
                _options
            );
            session.AudioOutputOffset = AudioOutputOffset;
            _current = (peer.PeerId, session);
        }

        LogReceiving(peer.PeerId);
        _ = StartAsync(peer.PeerId, session);
    }

    private async Task StartAsync(PeerId peer, IMediaSession session)
    {
        try
        {
            await session.StartAsync(_stop.Token).ConfigureAwait(false);
            SessionStarted?.Invoke(session);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Deliberately not logged: the subscriber is stopping.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogStartFailed(exception, peer);
            OnPeerLeft(peer);
        }
    }

    private void OnPeerLeft(PeerId peer)
    {
        IMediaSession? session = null;
        lock (_gate)
        {
            if (_current is { } current && current.Peer == peer)
            {
                session = current.Session;
                _current = null;
            }
        }

        if (session is not null)
        {
            LogPublisherLeft(peer);
            _ = session.DisposeAsync().AsTask();
        }
    }

    [LoggerMessage(2210, LogLevel.Information, "Receiving publisher {Peer}.")]
    private partial void LogReceiving(PeerId peer);

    [LoggerMessage(2211, LogLevel.Information, "Publisher {Peer} left.")]
    private partial void LogPublisherLeft(PeerId peer);

    [LoggerMessage(2212, LogLevel.Warning, "The session with publisher {Peer} failed to start.")]
    private partial void LogStartFailed(Exception exception, PeerId peer);
}
