using System.Collections.Immutable;
using System.Diagnostics;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.Streams;
using Agash.StreamTransport.Sync;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.Sessions;

/// <summary>What every media session of a host shares.</summary>
/// <param name="Codecs">The registered codecs and processors.</param>
/// <param name="Transports">Makes each session's transport.</param>
/// <param name="Clock">The media clock.</param>
/// <param name="Loggers">The logging.</param>
/// <param name="Metrics">The library's instruments.</param>
internal sealed record SessionServices(
    MediaCodecRegistry Codecs,
    IMediaTransportFactory Transports,
    MediaClock Clock,
    ILoggerFactory Loggers,
    StreamTransportMetrics Metrics
);

/// <summary>
/// A media session with one peer over a transport. It offers the registered codecs the endpoints need, and
/// once the transport connects builds a send stream for each source and a receive stream for each sink
/// from what was agreed. The transport's capacity, less what audio and loss recovery take, is the video
/// encoder's target; keyframe requests reach the encoder; received frames go to their stream.
/// </summary>
internal sealed partial class MediaSession : IMediaSession, IReceivedMediaConsumer
{
    private const long DefaultStartBitsPerSecond = 2_000_000;
    private const long MinimumVideoBitsPerSecond = 100_000;

    private readonly MediaSessionRole _role;
    private readonly MediaEndpoints _endpoints;
    private readonly MediaSessionOptions _options;
    private readonly SessionServices _services;
    private readonly IMediaTransport _transport;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _connected = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly Playout _playout;
    private readonly CaptureClock _captureClock;
    private readonly MediaRateAllocator _allocator;
    private Activity? _connecting;
    private int _disposed;
    private VideoSendStream? _videoSend;
    private AudioSendStream? _audioSend;
    private VideoReceiveStream? _videoReceive;
    private AudioReceiveStream? _audioReceive;

    public MediaSession(
        ISignalingChannel signaling,
        MediaSessionRole role,
        MediaEndpoints endpoints,
        MediaSessionOptions options,
        SessionServices services
    )
    {
        if (!endpoints.HasVideo && !endpoints.HasAudio)
        {
            throw new ArgumentException(
                "A session needs a video or audio source or sink.",
                nameof(endpoints)
            );
        }

        _role = role;
        _endpoints = endpoints;
        _options = options;
        _services = services;
        _logger = services.Loggers.CreateLogger<MediaSession>();
        _playout = new Playout(options, services.Clock, _logger, services.Metrics);
        _captureClock = new CaptureClock(services.Clock);
        _transport = services.Transports.Create(signaling, role, options.Transport);
        _allocator = new MediaRateAllocator(_transport.SentBytes, services.Clock.TimeProvider);
        _transport.StateChanged += OnStateChanged;
        _transport.CapacityChanged += OnCapacity;
        _transport.KeyframeRequested += OnKeyframeRequested;
        _transport.CircuitBreakerChanged += OnCircuitBreaker;
        services.Metrics.SessionsActive.Add(1, StreamTransportMetrics.Role(role));
    }

    public event Action<TransportState>? StateChanged;

    public event Action<CircuitBreakerState>? CircuitBreakerChanged;

    public Task Connected => _connected.Task;

    public TransportState State => _transport.State;

    public TransportStatistics Transport => _transport.Statistics;

    /// <summary>How the video's alpha travels, as negotiated; none until the media is built.</summary>
    internal AlphaLayout VideoAlpha { get; private set; }

    /// <inheritdoc/>
    public MediaRoute? Route => _transport.Route;

    public MediaSessionStatistics Statistics =>
        new(
            _videoSend?.FramesSent ?? 0,
            _audioSend?.FramesSent ?? 0,
            _audioReceive?.FramesDecoded ?? 0,
            _videoSend?.FramesDropped ?? 0,
            _videoReceive?.FramesDecoded ?? 0,
            _videoReceive?.FramesFailed ?? 0,
            _videoReceive?.FramesSkipped ?? 0,
            _audioReceive?.Concealed ?? 0,
            _audioReceive?.Recovered ?? 0,
            _playout.CurrentDelay
        );

    public TimeSpan AudioOutputOffset
    {
        get => _playout.AudioOutputOffset;
        set => _playout.AudioOutputOffset = value;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // From here to media flowing, or to the failure, is one span and one measurement.
        _connecting = StreamTransportDiagnostics.ActivitySource.StartActivity(
            "streamtransport.session.connect"
        );
        _connecting?.SetTag(
            "streamtransport.session.role",
            _role == MediaSessionRole.Offerer ? "offerer" : "answerer"
        );
        _connecting?.SetTag("streamtransport.session.video", _endpoints.HasVideo);
        _connecting?.SetTag("streamtransport.session.audio", _endpoints.HasAudio);
        MediaTime started = _services.Clock.Now;
        _ = _connected.Task.ContinueWith(
            (task, state) => ((MediaSession)state!).OnConnectSettled(task, started),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

        LogStarting(_role, _endpoints.HasVideo, _endpoints.HasAudio);
        _ = ConnectAsync(cancellationToken);
        return Task.CompletedTask;
    }

    public bool TryResumeTransmission() => _transport.TryResume();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _services.Metrics.SessionsActive.Add(-1, StreamTransportMetrics.Role(_role));
        if (_videoSend is not null)
        {
            await _videoSend.DisposeAsync().ConfigureAwait(false);
        }

        _audioSend?.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
        if (_videoReceive is not null)
        {
            await _videoReceive.DisposeAsync().ConfigureAwait(false);
        }

        if (_audioReceive is not null)
        {
            await _audioReceive.DisposeAsync().ConfigureAwait(false);
        }

        await _playout.DisposeAsync().ConfigureAwait(false);
        _ = _connected.TrySetCanceled();
        LogStopped();
    }

    void IReceivedMediaConsumer.OnVideoFrame(
        EncodedFrameBuffer frame,
        bool keyframe,
        NtpTime? capture
    )
    {
        if (Volatile.Read(ref _videoReceive) is { } stream)
        {
            stream.OnEncodedFrame(frame, keyframe, capture);
        }
        else
        {
            frame.Dispose();
        }
    }

    void IReceivedMediaConsumer.OnAudioPacket(
        EncodedFrameBuffer packet,
        long sequence,
        TimeSpan position,
        NtpTime? capture
    )
    {
        if (Volatile.Read(ref _audioReceive) is { } stream)
        {
            stream.OnPacket(packet, sequence, position, capture);
        }
        else
        {
            packet.Dispose();
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            NegotiatedMedia media = await _transport
                .ConnectAsync(BuildOffer(), this, cancellationToken)
                .ConfigureAwait(false);
            BuildStreams(media);
            _ = _connected.TrySetResult();
        }
        catch (OperationCanceledException)
        {
            _ = _connected.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogStreamsFailed(exception);
            _ = _connected.TrySetException(exception);
        }
    }

    // What this side can send and take: the options' codecs that a registered codec encodes where there is a
    // source and decodes where there is a sink, in the options' order.
    private MediaOffer BuildOffer()
    {
        AudioOffer? audio = null;
        if (_endpoints.HasAudio)
        {
            audio = new AudioOffer(
                [
                    .. _options
                        .AudioCodecs.Where(c =>
                            (_endpoints.AudioSource is null || _services.Codecs.CanEncode(c))
                            && (_endpoints.AudioSink is null || _services.Codecs.CanDecode(c))
                        )
                        .Select(static c => new AudioCodecFormat(c)),
                ],
                _endpoints.AudioSource is not null,
                _endpoints.AudioSink is not null
            );
        }

        VideoOffer? video = null;
        if (_endpoints.HasVideo)
        {
            video = new VideoOffer(
                [
                    .. _options
                        .VideoCodecs.Where(c =>
                            (_endpoints.VideoSource is null || _services.Codecs.CanEncode(c))
                            && (_endpoints.VideoSink is null || _services.Codecs.CanDecode(c))
                        )
                        .Select(c => new VideoCodecOffer(new VideoCodecFormat(c), AlphaWays(c))),
                ],
                _endpoints.VideoSource is not null,
                _endpoints.VideoSink is not null
            );
        }

        return new MediaOffer(video, audio);
    }

    // Builds the streams from what was agreed. Each media stands alone: a source or sink that cannot start
    // (a machine with no microphone) loses its own media and the others still flow; the session fails only
    // when none of them could be built.
    private void BuildStreams(NegotiatedMedia media)
    {
        int built = 0;
        Exception? failure = null;
        if (media.Video is { } video)
        {
            try
            {
                BuildVideo(video);
                built++;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogMediaFailed(exception, "video");
                failure ??= exception;
            }
        }

        if (media.Audio is { } audio)
        {
            try
            {
                BuildAudio(audio);
                built++;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogMediaFailed(exception, "audio");
                failure ??= exception;
            }
        }

        if (built == 0 && failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
    }

    private void BuildVideo(NegotiatedVideo video)
    {
        VideoAlpha = video.Alpha;
        if (video.Alpha != AlphaLayout.None)
        {
            LogAlpha(video.Alpha, video.Encode.Codec);
        }

        if (_endpoints.VideoSource is { } source)
        {
            long start = _transport.Capacity.TargetBitsPerSecond;

            // The stream is encoded for what the receiver declared it decodes: its profile and level.
            _videoSend = new VideoSendStream(
                source,
                new VideoSendSetup(
                    video.Encode,
                    start > 0 ? start : DefaultStartBitsPerSecond,
                    video.Alpha
                ),
                _services.Codecs,
                _options,
                SendVideo,
                _services.Clock,
                _services.Metrics,
                _logger
            );
        }

        if (_endpoints.VideoSink is { } sink)
        {
            Volatile.Write(
                ref _videoReceive,
                new VideoReceiveStream(
                    new VideoReceiveSetup(video.Decode, video.Alpha, RequestKeyframe),
                    sink,
                    _services.Codecs,
                    _playout,
                    _services.Clock,
                    _services.Metrics,
                    _logger
                )
            );
        }
    }

    private void BuildAudio(NegotiatedAudio audio)
    {
        if (_endpoints.AudioSource is { } source)
        {
            _audioSend = new AudioSendStream(
                source,
                audio.Format,
                _services.Codecs,
                _options,
                SendAudio,
                _services.Metrics
            );
        }

        if (_endpoints.AudioSink is { } sink)
        {
            Volatile.Write(
                ref _audioReceive,
                new AudioReceiveStream(
                    audio.Format,
                    sink,
                    _services.Codecs,
                    _playout,
                    _services.Clock,
                    _services.Metrics,
                    _logger
                )
            );
        }
    }

    private void SendVideo(in EncodedVideoFrame frame) =>
        _ = _transport.TrySendVideo(in frame, _captureClock.ToNtp(frame.Timestamp.Origin));

    private void SendAudio(in EncodedAudioFrame frame) =>
        _ = _transport.TrySendAudio(in frame, _captureClock.ToNtp(frame.Timestamp.Origin));

    // The ways this side can carry alpha for a codec, most preferred first. A sender lists them when its
    // options ask for alpha: the codec's alpha layer first when that is preferred and an encoder codes it,
    // and side by side, which every codec carries. A receiver lists what it can take: the layer when a
    // decoder decodes it, and side by side.
    private ImmutableArray<AlphaLayout> AlphaWays(VideoCodecId codec)
    {
        if (_endpoints.VideoSource is not null)
        {
            return _options.Alpha switch
            {
                AlphaLayout.Layer when _services.Codecs.CanEncodeAlphaLayer(codec) =>
                [
                    AlphaLayout.Layer,
                    AlphaLayout.PackSideBySide,
                ],
                AlphaLayout.Layer or AlphaLayout.PackSideBySide => [AlphaLayout.PackSideBySide],
                _ => [],
            };
        }

        return _endpoints.VideoSink is null ? []
            : _services.Codecs.CanDecodeAlphaLayer(codec)
                ? [AlphaLayout.Layer, AlphaLayout.PackSideBySide]
            : [AlphaLayout.PackSideBySide];
    }

    private void OnStateChanged(TransportState state)
    {
        LogState(state);
        StateChanged?.Invoke(state);
    }

    private void OnCapacity(CapacityEstimate estimate)
    {
        long audio = _audioSend is null ? 0 : _options.AudioBitsPerSecond;
        _videoSend?.SetBitrate(
            _allocator.VideoBitsPerSecond(
                estimate.TargetBitsPerSecond,
                audio,
                MinimumVideoBitsPerSecond
            )
        );
    }

    private void OnKeyframeRequested()
    {
        _services.Metrics.KeyframeRequests.Add(1, StreamTransportMetrics.Direction("received"));
        _videoSend?.RequestKeyframe();
    }

    private void OnCircuitBreaker(CircuitBreakerState state)
    {
        LogCircuitBreaker(state);
        CircuitBreakerChanged?.Invoke(state);
    }

    private void RequestKeyframe()
    {
        _services.Metrics.KeyframeRequests.Add(1, StreamTransportMetrics.Direction("sent"));
        _transport.RequestKeyframe();
    }

    // Records how the connect ended and closes its span.
    private void OnConnectSettled(Task connected, MediaTime started)
    {
        string outcome =
            connected.IsCompletedSuccessfully ? "connected"
            : connected.IsCanceled ? "canceled"
            : "failed";
        _services.Metrics.SessionConnectDuration.Record(
            (_services.Clock.Now - started).TotalSeconds,
            StreamTransportMetrics.Role(_role),
            StreamTransportMetrics.Outcome(outcome)
        );
        if (_connecting is { } activity)
        {
            activity.SetTag("streamtransport.outcome", outcome);
            if (connected.Exception?.InnerException is { } exception)
            {
                activity.AddException(exception);
                activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            }

            activity.Stop();
        }
    }

    [LoggerMessage(
        2100,
        LogLevel.Information,
        "Media session starting as {Role} (video {Video}, audio {Audio})."
    )]
    private partial void LogStarting(MediaSessionRole role, bool video, bool audio);

    [LoggerMessage(2101, LogLevel.Debug, "Transport {State}.")]
    private partial void LogState(TransportState state);

    [LoggerMessage(
        2104,
        LogLevel.Error,
        "The media streams could not be built for the negotiated media."
    )]
    private partial void LogStreamsFailed(Exception exception);

    [LoggerMessage(
        2112,
        LogLevel.Error,
        "The {Kind} stream could not be built; the session goes on without it."
    )]
    private partial void LogMediaFailed(Exception exception, string kind);

    [LoggerMessage(2113, LogLevel.Information, "Video alpha travels as {Alpha} in {Codec}.")]
    private partial void LogAlpha(AlphaLayout alpha, VideoCodecId codec);

    [LoggerMessage(2114, LogLevel.Warning, "Circuit breaker {State}.")]
    private partial void LogCircuitBreaker(CircuitBreakerState state);

    [LoggerMessage(2111, LogLevel.Information, "Media session stopped.")]
    private partial void LogStopped();
}
