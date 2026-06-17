using System.Diagnostics;
using Agash.StreamTransport.Transport;
using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.CongestionControl;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport;

/// <summary>
/// Sends audio and/or video to a peer over WebRTC: encodes frames pulled from an <see cref="IVideoFrameSource"/>
/// and an <see cref="IAudioFrameSource"/> with the <b>negotiated</b> codec, packetizes the access units with
/// that codec's RTP payload format, and sends them through <see cref="PeerConnection.SendRtp"/>. The codec,
/// payload type, and SSRC are resolved from <see cref="PeerConnection.NegotiatedMedia"/> once the offer/answer
/// completes - nothing here is hardwired to HEVC/Opus. Both tracks ride one peer connection so they stay in sync.
/// </summary>
public sealed partial class WebRtcMediaSender : IMediaSender
{
    private const long VideoBitrate = 6_000_000;

    private readonly MediaTransportOptions _options;
    private readonly IMediaCodecRegistry _registry;
    private readonly IDtlsTransportFactory _dtlsFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly INetworkController? _controller;
    private readonly PacingBudget? _pacer;
    private readonly MobilityEngine? _mobility;
    private readonly nint _gpuDeviceHandle;
    private RtcSession? _session;
    private IDisposable? _mobilityRegistration;

    // Resolved from the negotiated media once connected.
    private IVideoEncoder? _videoEncoder;
    private IRtpPacketizer? _videoPacketizer;
    private byte _videoPayloadType;
    private uint _videoSsrc;
    private IAudioEncoder? _audioEncoder;
    private byte _audioPayloadType;
    private uint _audioSsrc;

    private CancellationTokenSource? _cts;
    private Task? _audioLoop;
    private Task? _videoLoop;
    private long _videoBaseCaptureNs = -1;
    private uint _audioRtpTimestamp;

    /// <summary>
    /// Create a sender. At least one of <paramref name="video"/> or <paramref name="audio"/> must be non-null.
    /// The codec set (<paramref name="registry"/>), DTLS-SRTP engine (<paramref name="dtlsFactory"/>), and
    /// logging are injected so the sender is built entirely from pluggable services.
    /// <paramref name="gpuDeviceHandle"/> is an optional shared <c>ID3D11Device*</c> for zero-copy GPU encode.
    /// </summary>
    public WebRtcMediaSender(
        MediaTransportOptions options,
        IMediaCodecRegistry registry,
        IDtlsTransportFactory dtlsFactory,
        ILoggerFactory loggerFactory,
        IVideoFrameSource? video = null,
        IAudioFrameSource? audio = null,
        nint gpuDeviceHandle = 0,
        INetworkController? controller = null,
        MobilityEngine? mobility = null)
    {
        if (video is null && audio is null)
        {
            throw new ArgumentException("A sender needs at least a video or an audio source.");
        }

        _options = options;
        _registry = registry;
        _dtlsFactory = dtlsFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<WebRtcMediaSender>();
        _controller = controller;
        _pacer = controller is null ? null : new PacingBudget(controller.CurrentEstimate.PacingRateBps);
        _mobility = mobility;
        _gpuDeviceHandle = gpuDeviceHandle;
        VideoSource = video;
        AudioSource = audio;
    }

    /// <summary>The video frame source, or null for audio-only.</summary>
    public IVideoFrameSource? VideoSource { get; }

    /// <summary>The audio frame source, or null for video-only.</summary>
    public IAudioFrameSource? AudioSource { get; }

    /// <inheritdoc/>
    public async Task StartAsync(ISignalingChannel signaling, CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _session = new RtcSession(
            signaling, MediaConfig.Build(_registry, _options, AudioSource is not null, VideoSource is not null), _dtlsFactory, _loggerFactory, _controller);
        PeerConnection pc = _session.Pc;

        // Congestion control: the controller's estimate retunes the encoder and the pacer live.
        pc.BitrateEstimateChanged += OnBitrateEstimate;

        // Mobility: a network change re-probes this connection's path, preserving the SRTP session.
        _mobilityRegistration = _mobility?.Register(() => _session?.Pc.TriggerNetworkRecovery());

        pc.StateChanged += state =>
        {
            LogStateChanged(state);
            if (state != PeerConnectionState.Connected)
            {
                return;
            }

            // The negotiated codec/payload type/SSRC are known now (offer/answer is complete). Build the
            // encoder + payload format from the agreed codec, then start the pumps once.
            ResolveNegotiatedSendMedia(pc);

            if (_audioEncoder is not null && _audioLoop is null)
            {
                _audioLoop = Task.Run(() => PumpAudioAsync(pc, _cts.Token));
            }

            if (_videoEncoder is not null && _videoLoop is null)
            {
                _videoLoop = Task.Run(() => PumpVideoAsync(pc, _cts.Token));
            }
        };

        LogStarting(AudioSource is not null, VideoSource is not null);
        _session.Listen();
        await _session.StartAsOffererAsync(_cts.Token).ConfigureAwait(false);
    }

    // Map the negotiated media to concrete encoders + payload formats. The first agreed codec per kind wins
    // (it is the mutually-preferred one); a kind with a source but no agreed/known codec is simply not sent.
    private void ResolveNegotiatedSendMedia(PeerConnection pc)
    {
        foreach (NegotiatedMediaInfo media in pc.NegotiatedMedia)
        {
            if (media.Codecs.Count == 0)
            {
                continue;
            }

            WebRtc.Sdp.SdpCodec codec = media.Codecs[0];
            if (media.Kind == WebRtc.Sdp.SdpMediaKind.Video && VideoSource is not null && _videoEncoder is null
                && _registry.FindVideo(codec.EncodingName) is { } videoCodec)
            {
                // Start the encoder at the controller's initial estimate (so the encoder and BWE agree from
                // the first frame and ramp together); fall back to the fixed default with no controller.
                long startBitrate = _controller?.CurrentEstimate.TargetBitrateBps ?? VideoBitrate;
                _videoEncoder = videoCodec.CreateEncoder(
                    new VideoEncoderSettings(_options.VideoFps, startBitrate, _options.VideoEncoderName, _gpuDeviceHandle, _options.PreserveAlpha, _options.MaxVideoBFrames));
                _videoPacketizer = videoCodec.CreatePacketizer();
                _videoPayloadType = (byte)codec.PayloadType;
                _videoSsrc = media.LocalSsrc;
                LogNegotiated("video", codec.EncodingName, codec.PayloadType);
            }
            else if (media.Kind == WebRtc.Sdp.SdpMediaKind.Audio && AudioSource is not null && _audioEncoder is null
                && _registry.FindAudio(codec.EncodingName) is { } audioCodec)
            {
                _audioEncoder = audioCodec.CreateEncoder();
                _audioPayloadType = (byte)codec.PayloadType;
                _audioSsrc = media.LocalSsrc;
                LogNegotiated("audio", codec.EncodingName, codec.PayloadType);
            }
        }
    }

    // Apply a new congestion estimate: retune the live encoder and the pacer. Cheap; called on the
    // controller's timer / feedback thread.
    private void OnBitrateEstimate(BitrateEstimate estimate)
    {
        _videoEncoder?.UpdateBitrate(estimate.TargetBitrateBps);
        _pacer?.SetRate(estimate.PacingRateBps);
        TransportHealthMetrics health = _session?.Pc.CurrentHealth ?? default;
        // queueDelay = smoothed - base RTT: the standing one-way queue SCReAM builds, i.e. the actual
        // delay-based congestion signal. queueDelay near 0 with low loss means the link is not the limit even
        // when the rate is capped; a rising queueDelay is real congestion. baseRtt is the link's floor RTT.
        double baseRttMs = health.BaseRttMicros / 1000.0;
        double rttMs = estimate.SmoothedRttMicros / 1000.0;
        LogHealth(estimate.TargetBitrateBps / 1000, estimate.PacingRateBps / 1000, rttMs, baseRttMs,
            Math.Max(0, rttMs - baseRttMs), health.LossRate * 100);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Congestion: target {TargetKbps} kbps, pacing {PacingKbps} kbps, rtt {RttMs:F1} ms (base {BaseRttMs:F1}, queue {QueueMs:F1}), loss {LossPercent:F1}%.")]
    private partial void LogHealth(long targetKbps, long pacingKbps, double rttMs, double baseRttMs, double queueMs, double lossPercent);

    // Pace one packet onto the link: block briefly while the budget is short, so a bursty intra frame is
    // smoothed instead of dumped (the cellular bufferbloat guard). No-op without a controller/pacer.
    private async Task PaceAsync(int bytes, CancellationToken cancellationToken)
    {
        if (_pacer is not { } pacer)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested && pacer.Refill(NowMicros()) < bytes)
        {
            if (!await DelayAsync(2, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        pacer.Consume(bytes);
    }

    private static long NowMicros() => Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;

    [LoggerMessage(Level = LogLevel.Information, Message = "Sender starting (audio={Audio}, video={Video}).")]
    private partial void LogStarting(bool audio, bool video);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sender peer connection state: {State}.")]
    private partial void LogStateChanged(PeerConnectionState state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sender negotiated {Kind} codec {Codec} (PT {PayloadType}).")]
    private partial void LogNegotiated(string kind, string codec, int payloadType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sender stopped.")]
    private partial void LogStopped();

    private async Task PumpAudioAsync(PeerConnection pc, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (AudioSource!.TryGetFrame(out AudioFrame frame))
            {
                EncodedAudioPacket encoded = _audioEncoder!.Encode(frame);
                _audioRtpTimestamp += encoded.DurationRtpUnits;
                await pc.SendRtp(_audioPayloadType, _audioSsrc, _audioRtpTimestamp, marker: false, encoded.Payload, MediaConfig.CaptureNsToNtp(frame.PresentationTimeNs), cancellationToken).ConfigureAwait(false);
            }
            else if (!await DelayAsync(5, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task PumpVideoAsync(PeerConnection pc, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (VideoSource!.TryGetFrame(out VideoFrame frame))
            {
                if (_videoEncoder!.Encode(frame) is { } encoded)
                {
                    // The RTP timestamp is each frame's absolute sampling instant mapped to the 90 kHz clock -
                    // NOT an accumulated delta. With B-frames the encoder emits access units in decode order
                    // with non-monotonic capture times; accumulating deltas would corrupt them, so the receiver
                    // (and its B-frame reorder) gets the true per-frame timestamp instead.
                    if (_videoBaseCaptureNs < 0)
                    {
                        _videoBaseCaptureNs = encoded.CaptureNs;
                    }

                    uint videoRtpTimestamp = (uint)((encoded.CaptureNs - _videoBaseCaptureNs) * MediaConfig.VideoClockRate / 1_000_000_000L);
                    ulong captureNtp = MediaConfig.CaptureNsToNtp(encoded.CaptureNs);
                    int count = _videoPacketizer!.Packetize(encoded.AccessUnit);
                    for (int i = 0; i < count; i++)
                    {
                        ReadOnlyMemory<byte> payload = _videoPacketizer.GetPayload(i);
                        bool isLast = i == count - 1;
                        await PaceAsync(payload.Length, cancellationToken).ConfigureAwait(false);
                        // abs-capture-time on the first packet of the access unit only.
                        await pc.SendRtp(_videoPayloadType, _videoSsrc, videoRtpTimestamp,
                            marker: isLast, payload, i == 0 ? captureNtp : 0, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            else if (!await DelayAsync(2, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private static async Task<bool> DelayAsync(int milliseconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _mobilityRegistration?.Dispose();

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        foreach (Task? loop in new[] { _audioLoop, _videoLoop })
        {
            if (loop is not null)
            {
                try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }

        _videoEncoder?.Dispose();
        _audioEncoder?.Dispose();

        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }

        LogStopped();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}
