using Agash.StreamTransport.Transport;
using System.Diagnostics;
using System.Buffers;
using System.Threading.Channels;
using Agash.StreamTransport.Sync;
using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.Rtp;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport;

/// <summary>
/// Receives audio and/or video from a peer over WebRTC. <see cref="PeerConnection.RtpReceived"/> fires on
/// the transport's receive thread; this depacketizes there (cheap) with the <b>negotiated</b> codec's payload
/// format and hands the assembled access unit / audio packet to a decode worker over a channel, so decoding
/// never stalls the receive loop. The codec, payload type, and decoder are resolved from
/// <see cref="PeerConnection.NegotiatedMedia"/> once connected - nothing is hardwired to HEVC/Opus.
/// </summary>
/// <remarks>
/// In <see cref="PlayoutMode.Synced"/> (both streams, CPU decode) it lip-syncs audio and video through a
/// capture-clock <see cref="RtpClockAligner"/> + <see cref="PlayoutScheduler"/>, anchored by the
/// abs-capture-time the sender stamps; otherwise it presents on arrival.
/// </remarks>
public sealed partial class WebRtcMediaReceiver : IMediaReceiver
{
    private readonly MediaTransportOptions _options;
    private readonly IMediaCodecRegistry _registry;
    private readonly IDtlsTransportFactory _dtlsFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Channel<(PooledBuffer Buffer, uint Timestamp)> _videoQueue =
        Channel.CreateUnbounded<(PooledBuffer, uint)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Channel<(PooledBuffer Buffer, uint Timestamp)> _audioQueue =
        Channel.CreateUnbounded<(PooledBuffer, uint)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private const int VideoClockRate = 90_000;
    private const int AudioClockRate = 48_000;

    private readonly RtpClockAligner _aligner = new();
    private RtcSession? _session;

    // Resolved from the negotiated media once connected.
    private IVideoDecoder? _video;
    private IRtpDepacketizer? _videoDepacketizer;
    private byte _videoPayloadType;
    private IAudioDecoder? _audio;
    private byte _audioPayloadType;

    private PlayoutScheduler? _scheduler;
    private bool _syncEnabled;
    private bool _gpuAsymmetricSync;
    private bool _preserveAlpha;
    private Task? _videoDecodeLoop;
    private Task? _audioDecodeLoop;

    /// <summary>
    /// Create a receiver. At least one of <paramref name="video"/> or <paramref name="audio"/> must be non-null.
    /// The codec set (<paramref name="registry"/>), DTLS-SRTP engine (<paramref name="dtlsFactory"/>), and
    /// logging are injected so the receiver is built entirely from pluggable services.
    /// </summary>
    public WebRtcMediaReceiver(
        MediaTransportOptions options,
        IMediaCodecRegistry registry,
        IDtlsTransportFactory dtlsFactory,
        ILoggerFactory loggerFactory,
        IVideoFrameSink? video = null,
        IAudioFrameSink? audio = null)
    {
        if (video is null && audio is null)
        {
            throw new ArgumentException("A receiver needs at least a video or an audio sink.");
        }

        _options = options;
        _registry = registry;
        _dtlsFactory = dtlsFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<WebRtcMediaReceiver>();
        _preserveAlpha = options.PreserveAlpha;
        VideoSink = video;
        AudioSink = audio;
    }

    /// <summary>The video sink decoded frames are submitted to, or null for audio-only.</summary>
    public IVideoFrameSink? VideoSink { get; }

    /// <summary>The audio sink decoded frames are submitted to, or null for video-only.</summary>
    public IAudioFrameSink? AudioSink { get; }

    /// <summary>Adopt the publisher's negotiated side-by-side-alpha setting (no-op for audio-only).</summary>
    public void SetPreserveAlpha(bool value)
    {
        _preserveAlpha = value;
        _video?.SetPreserveAlpha(value);
    }

    /// <inheritdoc/>
    public Task StartAsync(ISignalingChannel signaling, CancellationToken cancellationToken = default)
    {
        _session = new RtcSession(
            signaling, MediaConfig.Build(_registry, _options, AudioSink is not null, VideoSink is not null), _dtlsFactory, _loggerFactory);
        PeerConnection pc = _session.Pc;

        pc.StateChanged += state =>
        {
            LogStateChanged(state);
            if (state == PeerConnectionState.Connected)
            {
                StartDecoding(pc, cancellationToken);
            }
        };
        pc.RtpReceived += OnRtpReceived;
        LogStarting(AudioSink is not null, VideoSink is not null);
        _session.Listen();
        return Task.CompletedTask;
    }

    // Build the decoders + payload formats from the negotiated media (codec/PT known now), wire sync, and
    // start the decode workers. Runs once, on Connected, before media flows.
    private void StartDecoding(PeerConnection pc, CancellationToken cancellationToken)
    {
        if (_videoDecodeLoop is not null || _audioDecodeLoop is not null)
        {
            return;
        }

        foreach (NegotiatedMediaInfo media in pc.NegotiatedMedia)
        {
            if (media.Codecs.Count == 0)
            {
                continue;
            }

            WebRtc.Sdp.SdpCodec codec = media.Codecs[0];
            if (media.Kind == WebRtc.Sdp.SdpMediaKind.Video && VideoSink is not null && _video is null
                && _registry.FindVideo(codec.EncodingName) is { } videoCodec)
            {
                _video = videoCodec.CreateDecoder(new VideoDecoderSettings(_options.PreferGpuVideoOutput, _preserveAlpha));
                _videoDepacketizer = videoCodec.CreateDepacketizer();
                _videoPayloadType = (byte)codec.PayloadType;
                LogNegotiated("video", codec.EncodingName, codec.PayloadType);
            }
            else if (media.Kind == WebRtc.Sdp.SdpMediaKind.Audio && AudioSink is not null && _audio is null
                && _registry.FindAudio(codec.EncodingName) is { } audioCodec)
            {
                _audio = audioCodec.CreateDecoder();
                _audioPayloadType = (byte)codec.PayloadType;
                LogNegotiated("audio", codec.EncodingName, codec.PayloadType);
            }
        }

        // Synced playout engages with both streams once anchored by abs-capture-time. On the CPU decode path the
        // scheduler holds both streams and releases them on one timeline. The GPU output texture is the decoder's
        // single reused surface and cannot be held, so the GPU path syncs asymmetrically: video presents on
        // arrival and defines the timeline (ObserveArrival), audio is delayed onto it (ScheduleOnTimeline). Until
        // both streams are anchored, frames present on arrival.
        _syncEnabled = _options.PlayoutMode == PlayoutMode.Synced
            && _audio is not null && _video is not null;
        _gpuAsymmetricSync = _syncEnabled && _video!.IsGpuOutput;
        if (_syncEnabled)
        {
            _scheduler = new PlayoutScheduler(
                (long)(_options.MinPlayoutDelay.TotalSeconds * 1_000_000_000),
                (long)(_options.MaxPlayoutDelay.TotalSeconds * 1_000_000_000),
                (long)(_options.PlayoutMargin.TotalSeconds * 1_000_000_000));
        }

        if (_audio is not null)
        {
            _audioDecodeLoop = Task.Run(() => DecodeAudioAsync(cancellationToken), cancellationToken);
        }

        if (_video is not null)
        {
            _videoDecodeLoop = Task.Run(() => DecodeVideoAsync(cancellationToken), cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Receiver starting (audio={Audio}, video={Video}).")]
    private partial void LogStarting(bool audio, bool video);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Receiver peer connection state: {State}.")]
    private partial void LogStateChanged(PeerConnectionState state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Receiver negotiated {Kind} codec {Codec} (PT {PayloadType}).")]
    private partial void LogNegotiated(string kind, string codec, int payloadType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Receiver stopped.")]
    private partial void LogStopped();

    // Runs on the transport receive thread. Demux by the negotiated payload type, depacketize (cheap, copies
    // into the assembled unit) and enqueue; never decode here. The payload is a borrowed buffer for this call.
    private void OnRtpReceived(RtpHeader header, ReadOnlyMemory<byte> payload)
    {
        if (_videoDepacketizer is not null && header.PayloadType == _videoPayloadType)
        {
            if (_syncEnabled && header.AbsoluteCaptureTimeNtp is { } videoNtp && videoNtp != 0)
            {
                _aligner.RecordAbsCaptureTime(SyncStream.Video, videoNtp, header.Timestamp, VideoClockRate);
            }

            // Depacketize on the receive thread (cheap) into a pool-rented buffer whose ownership passes through
            // the queue to the decode worker, which returns it after decode. If the queue rejects it, return it.
            PooledBuffer? accessUnit = _videoDepacketizer.Push(payload.Span, header.Marker);
            if (accessUnit is { } videoUnit && !_videoQueue.Writer.TryWrite((videoUnit, header.Timestamp)))
            {
                videoUnit.Dispose();
            }
        }
        else if (_audio is not null && header.PayloadType == _audioPayloadType)
        {
            if (_syncEnabled && header.AbsoluteCaptureTimeNtp is { } audioNtp && audioNtp != 0)
            {
                _aligner.RecordAbsCaptureTime(SyncStream.Audio, audioNtp, header.Timestamp, AudioClockRate);
            }

            // Copy the borrowed payload into a pool-rented buffer for the cross-thread hand-off to the decoder.
            byte[] rented = ArrayPool<byte>.Shared.Rent(payload.Length);
            payload.Span.CopyTo(rented);
            var audioUnit = new PooledBuffer(rented, payload.Length);
            if (!_audioQueue.Writer.TryWrite((audioUnit, header.Timestamp)))
            {
                audioUnit.Dispose();
            }
        }
    }

    // Present on arrival, or - when synced and both stream clocks are anchored - schedule by capture time so
    // audio and video lip-sync.
    private void Present(SyncStream stream, uint rtpTimestamp, Action submit)
    {
        if (_syncEnabled && _scheduler is { } scheduler && _aligner.BothAligned
            && _aligner.TryToSenderWallNs(stream, rtpTimestamp, out long wallNs))
        {
            if (_gpuAsymmetricSync)
            {
                // GPU: video can't be held, so present it now and let its arrival define the timeline; audio is
                // delayed onto that timeline so it lip-syncs with the on-arrival video.
                if (stream == SyncStream.Video)
                {
                    scheduler.ObserveArrival(wallNs);
                    submit();
                }
                else
                {
                    scheduler.ScheduleOnTimeline(wallNs, submit);
                }
            }
            else
            {
                scheduler.Schedule(wallNs, submit);
            }
        }
        else
        {
            submit();
        }
    }

    private async Task DecodeVideoAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach ((PooledBuffer accessUnit, uint timestamp) in _videoQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                using (accessUnit)
                {
                    // Schedule against the access unit that produced this frame (frameRtp), not the one just
                    // submitted: the decoder holds a pipeline (and B-frame reorder), so the content emerging now
                    // belongs to an earlier timestamp. The decoded frame owns its own pixels, so the access-unit
                    // buffer is returned to the pool here, before any scheduled submit.
                    if (VideoSink is { } sink && _video!.Decode(accessUnit.Memory.Span, timestamp, NowNs(), out uint frameRtp) is { } frame)
                    {
                        Present(SyncStream.Video, frameRtp, () => sink.Submit(frame));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DecodeAudioAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach ((PooledBuffer payload, uint timestamp) in _audioQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                using (payload)
                {
                    AudioFrame frame = _audio!.Decode(payload.Memory.Span, NowNs());
                    if (AudioSink is { } sink)
                    {
                        Present(SyncStream.Audio, timestamp, () => sink.Submit(frame));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static long NowNs() => Stopwatch.GetTimestamp() * (1_000_000_000L / Stopwatch.Frequency);

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _videoQueue.Writer.TryComplete();
        _audioQueue.Writer.TryComplete();

        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }

        foreach (Task? loop in new[] { _videoDecodeLoop, _audioDecodeLoop })
        {
            if (loop is not null)
            {
                try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }

        if (_scheduler is not null)
        {
            await _scheduler.DisposeAsync().ConfigureAwait(false);
        }

        _video?.Dispose();
        _audio?.Dispose();
        _videoDepacketizer?.Dispose();
        LogStopped();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
