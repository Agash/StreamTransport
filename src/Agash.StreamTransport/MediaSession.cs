using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport;

/// <summary>
/// What a session sends and where what it receives goes. A session sends what has a source and receives
/// what has a sink; at least one of the four is needed.
/// </summary>
public sealed record MediaEndpoints
{
    /// <summary>The video to send.</summary>
    public IVideoSource? VideoSource { get; init; }

    /// <summary>The audio to send.</summary>
    public IAudioSource? AudioSource { get; init; }

    /// <summary>Where received video goes.</summary>
    public IVideoSink? VideoSink { get; init; }

    /// <summary>Where received audio goes.</summary>
    public IAudioSink? AudioSink { get; init; }

    /// <summary>Whether the endpoints send or receive video.</summary>
    public bool HasVideo => VideoSource is not null || VideoSink is not null;

    /// <summary>Whether the endpoints send or receive audio.</summary>
    public bool HasAudio => AudioSource is not null || AudioSink is not null;
}

/// <summary>Counters of a session's media, for telemetry.</summary>
/// <param name="VideoFramesSent">Encoded video frames handed to the link.</param>
/// <param name="AudioFramesSent">Encoded audio frames handed to the link.</param>
/// <param name="AudioFramesDecoded">Received audio frames decoded.</param>
/// <param name="VideoFramesDropped">Source frames dropped because the encoder was busy.</param>
/// <param name="VideoFramesDecoded">Received video frames decoded.</param>
/// <param name="VideoFramesFailed">Received video frames the decoder rejected.</param>
/// <param name="VideoFramesSkipped">Received video frames dropped undecoded because the decoder or output fell behind.</param>
/// <param name="AudioConcealed">Audio gaps filled by concealment.</param>
/// <param name="AudioRecovered">Lost audio packets rebuilt from redundancy.</param>
/// <param name="PlayoutDelay">The synced playout buffer depth; zero when frames play on arrival.</param>
/// <param name="AvSyncOffset">
/// The lip sync measured where frames reach the sinks, smoothed: the capture instant of the video shown less
/// that of the audio heard with it, positive when audio lags video; null until both have played.
/// </param>
public readonly record struct MediaSessionStatistics(
    int VideoFramesSent,
    int AudioFramesSent,
    int AudioFramesDecoded,
    int VideoFramesDropped,
    int VideoFramesDecoded,
    int VideoFramesFailed,
    int VideoFramesSkipped,
    int AudioConcealed,
    int AudioRecovered,
    TimeSpan PlayoutDelay,
    TimeSpan? AvSyncOffset
);

/// <summary>A media session with one peer.</summary>
public interface IMediaSession : IAsyncDisposable
{
    /// <summary>Completes when media can flow; faults when the session fails to connect.</summary>
    Task Connected { get; }

    /// <summary>Where the transport's connection stands.</summary>
    TransportState State { get; }

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    event Action<TransportState>? StateChanged;

    /// <summary>
    /// Raised when the transport's circuit breaker opens, reduces or closes. Once open, media stays stopped
    /// until <see cref="TryResumeTransmission"/>.
    /// </summary>
    event Action<CircuitBreakerState>? CircuitBreakerChanged;

    /// <summary>The transport's counters and estimates: capacity, loss, recovery, the circuit breaker.</summary>
    TransportStatistics Transport { get; }

    /// <summary>Counters of the session's media.</summary>
    MediaSessionStatistics Statistics { get; }

    /// <summary>The network path media takes, once one is chosen; null before then.</summary>
    MediaRoute? Route { get; }

    /// <summary>
    /// How much later received audio plays than its synced slot: the video output path's latency less
    /// the audio output's, so the two reach the viewer together. Negative plays audio earlier.
    /// </summary>
    TimeSpan AudioOutputOffset { get; set; }

    /// <summary>Starts negotiating over the session's signaling channel.</summary>
    /// <param name="cancellationToken">Cancels starting.</param>
    /// <returns>A task that completes once negotiation has started.</returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes media after the circuit breaker stopped it, when the application has reason to think the
    /// problem passed; refused until as long as it took to trip has passed again.
    /// </summary>
    /// <returns>Whether media flows again.</returns>
    bool TryResumeTransmission();
}

/// <summary>
/// Makes media sessions. A session runs over the registered <see cref="IMediaTransportFactory"/>'s
/// transport, so rooms, publishers and subscribers work over any transport unchanged.
/// </summary>
public interface IMediaSessionFactory
{
    /// <summary>Makes a session with one peer.</summary>
    /// <param name="signaling">The channel negotiation runs over.</param>
    /// <param name="role">Whether this side offers or answers.</param>
    /// <param name="endpoints">What is sent and where what is received goes.</param>
    /// <param name="options">How the session is set up.</param>
    /// <returns>The session, not yet started.</returns>
    IMediaSession Create(
        ISignalingChannel signaling,
        MediaSessionRole role,
        MediaEndpoints endpoints,
        MediaSessionOptions options
    );
}
