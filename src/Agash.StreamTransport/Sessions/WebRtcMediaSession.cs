using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.Rtp;
using Agash.StreamTransport.Streams;
using Agash.StreamTransport.Sync;
using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.DependencyInjection;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;
using Agash.StreamTransport.WebRtc.Sdp;
using Microsoft.Extensions.Logging;
using WebRtcIceCandidate = Agash.StreamTransport.WebRtc.Ice.IceCandidate;

namespace Agash.StreamTransport.Sessions;

/// <summary>What every WebRTC session of a host shares.</summary>
/// <param name="Codecs">The registered codecs and processors.</param>
/// <param name="PayloadFormats">The registered RTP payload formats.</param>
/// <param name="Connections">Makes peer connections.</param>
/// <param name="Clock">The media clock.</param>
/// <param name="Loggers">The logging.</param>
/// <param name="Mobility">Re-probes connections when the host's networks change; null for none.</param>
internal sealed record SessionServices(
    MediaCodecRegistry Codecs,
    RtpPayloadFormatRegistry PayloadFormats,
    PeerConnectionFactory Connections,
    MediaClock Clock,
    ILoggerFactory Loggers,
    MobilityEngine? Mobility
);

/// <summary>
/// A media session over one WebRTC peer connection. It offers the registered codecs the endpoints need,
/// and once connected builds a send stream for each source and a receive stream for each sink from what
/// was negotiated. Congestion estimates retune the encoder and the pacer; keyframe requests reach the
/// encoder; received RTP goes to the stream of its payload type.
/// </summary>
internal sealed partial class WebRtcMediaSession : IMediaSession
{
    // RTP payload size that fits a 1280-byte IPv6 minimum MTU after IP, UDP, RTP, extension and SRTP.
    private const int MaxPayloadSize = 1100;
    private const int FirstVideoPayloadType = 96;
    private const byte VideoRtxPayloadType = 97;
    private const int AudioPayloadType = 111;
    private const long DefaultStartBitsPerSecond = 2_000_000;
    private const long MinimumVideoBitsPerSecond = 100_000;

    private readonly ISignalingChannel _signaling;
    private readonly MediaSessionRole _role;
    private readonly MediaEndpoints _endpoints;
    private readonly MediaSessionOptions _options;
    private readonly SessionServices _services;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _connected = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly Lock _gate = new();
    private readonly Playout _playout;
    private readonly CaptureClock _captureClock;
    private PeerConnection? _connection;
    private RtpPacer? _pacer;
    private IDisposable? _mobility;
    private VideoSendStream? _videoSend;
    private AudioSendStream? _audioSend;
    private VideoReceiveStream? _videoReceive;
    private AudioReceiveStream? _audioReceive;
    private int _videoPayloadType = -1;
    private int _audioPayloadType = -1;
    private uint _remoteVideoSsrc;
    private bool _streamsBuilt;

    public WebRtcMediaSession(
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

        _signaling = signaling;
        _role = role;
        _endpoints = endpoints;
        _options = options;
        _services = services;
        _logger = services.Loggers.CreateLogger<WebRtcMediaSession>();
        _playout = new Playout(options, services.Clock, _logger);
        _captureClock = new CaptureClock(services.Clock);
    }

    public event Action<PeerConnectionState>? StateChanged;

    public Task Connected => _connected.Task;

    public PeerConnectionState State => _connection?.State ?? PeerConnectionState.New;

    public TransportHealthMetrics Health => _connection?.CurrentHealth ?? default;

    /// <inheritdoc/>
    public MediaRoute? Route =>
        _connection?.SelectedPath is { } path ? new MediaRoute(path.Local, path.Remote) : null;

    public TransportLossStats LossStats => _connection?.CurrentLossStats ?? default;

    public MediaSessionStatistics Statistics =>
        new(
            _videoSend?.FramesSent ?? 0,
            _audioSend?.FramesSent ?? 0,
            _audioReceive?.FramesDecoded ?? 0,
            _videoSend?.FramesDropped ?? 0,
            _videoReceive?.FramesDecoded ?? 0,
            _videoReceive?.FramesFailed ?? 0,
            _audioReceive?.Concealed ?? 0,
            _audioReceive?.Recovered ?? 0,
            _playout.CurrentDelay
        );

    public TimeSpan AudioOutputOffset
    {
        get => _playout.AudioOutputOffset;
        set => _playout.AudioOutputOffset = value;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        (List<IPEndPoint> stun, List<string> unresolved) = await StunServers
            .ResolveAsync(_options.IceServers, cancellationToken)
            .ConfigureAwait(false);
        foreach (string url in unresolved)
        {
            LogStunUnresolved(url);
        }

        PeerConnection connection = _services.Connections.Create(BuildOptions(stun));
        lock (_gate)
        {
            _connection = connection;
        }

        _pacer = new RtpPacer(
            SendAsync,
            connection.CurrentBitrateEstimate.PacingRateBps,
            _services.Clock.TimeProvider,
            _logger
        );
        connection.LocalIceCandidate += OnLocalCandidate;
        connection.StateChanged += OnStateChanged;
        connection.RtpReceived += OnRtp;
        connection.KeyframeRequested += _ => _videoSend?.RequestKeyframe();
        connection.BitrateEstimateChanged += OnBitrateEstimate;
        _signaling.DescriptionReceived += OnDescriptionAsync;
        _signaling.IceCandidateReceived += OnCandidateAsync;
        _mobility = _services.Mobility?.Register(connection.TriggerNetworkRecovery);

        LogStarting(_role, _endpoints.HasVideo, _endpoints.HasAudio);
        if (_role == MediaSessionRole.Offerer)
        {
            await SendDescriptionAsync(SdpKind.Offer, connection.CreateOffer(), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _signaling.DescriptionReceived -= OnDescriptionAsync;
        _signaling.IceCandidateReceived -= OnCandidateAsync;
        _mobility?.Dispose();
        if (_videoSend is not null)
        {
            await _videoSend.DisposeAsync().ConfigureAwait(false);
        }

        _audioSend?.Dispose();
        if (_videoReceive is not null)
        {
            await _videoReceive.DisposeAsync().ConfigureAwait(false);
        }

        if (_audioReceive is not null)
        {
            await _audioReceive.DisposeAsync().ConfigureAwait(false);
        }

        await _playout.DisposeAsync().ConfigureAwait(false);
        if (_pacer is not null)
        {
            await _pacer.DisposeAsync().ConfigureAwait(false);
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _ = _connected.TrySetCanceled();
        LogStopped();
    }

    // The media lines: each kind the endpoints use, with the codecs both the registries and the payload
    // formats support, in the options' order.
    private PeerConnectionOptions BuildOptions(List<IPEndPoint> stun)
    {
        List<MediaLine> lines = [];
        if (_endpoints.HasAudio)
        {
            List<SdpCodec> codecs = [];
            int payloadType = AudioPayloadType;
            foreach (AudioCodecId codec in _options.AudioCodecs)
            {
                if (
                    _services.PayloadFormats.TryGet(codec.Name, out RtpPayloadFormat? format)
                    && format.Kind == SdpMediaKind.Audio
                    && (_endpoints.AudioSource is null || _services.Codecs.CanEncode(codec))
                    && (_endpoints.AudioSink is null || _services.Codecs.CanDecode(codec))
                )
                {
                    codecs.Add(format.ToSdpCodec(payloadType++));
                }
            }

            lines.Add(new MediaLine("0", SdpMediaKind.Audio, NewSsrc(), codecs));
        }

        if (_endpoints.HasVideo)
        {
            List<SdpCodec> codecs = [];
            int payloadType = FirstVideoPayloadType;
            foreach (VideoCodecId codec in _options.VideoCodecs)
            {
                if (
                    !_services.PayloadFormats.TryGet(codec.Name, out RtpPayloadFormat? format)
                    || format.Kind != SdpMediaKind.Video
                    || (_endpoints.VideoSource is not null && !_services.Codecs.CanEncode(codec))
                    || (_endpoints.VideoSink is not null && !_services.Codecs.CanDecode(codec))
                )
                {
                    continue;
                }

                if (payloadType == VideoRtxPayloadType)
                {
                    payloadType++;
                }

                var offered = format.ToSdpCodec(payloadType++);
                if (
                    _endpoints.VideoSource is not null
                    && _options.Alpha == AlphaLayout.PackSideBySide
                )
                {
                    offered = offered with
                    {
                        FormatParameters = FormatParameters.Append(
                            offered.FormatParameters,
                            FormatParameters.AlphaName,
                            FormatParameters.SideBySide
                        ),
                    };
                }

                codecs.Add(offered);
            }

            uint ssrc = NewSsrc();
            lines.Add(
                new MediaLine("1", SdpMediaKind.Video, ssrc, codecs)
                {
                    RtxSsrc = NewSsrc(),
                    RtxPayloadType = VideoRtxPayloadType,
                }
            );
        }

        return new PeerConnectionOptions
        {
            Media = lines,
            StunServers = stun,
            IncludeLoopback = _options.IncludeLoopbackCandidates,
            LocalAddressPreferences = _options.LocalAddressPreferences,
            EnableFec = _options.EnableFec && _endpoints.VideoSource is not null,
            FecProtectedSsrc =
                lines.FirstOrDefault(l => l.Kind == SdpMediaKind.Video)?.LocalSsrc ?? 0,
        };
    }

    private void OnStateChanged(PeerConnectionState state)
    {
        LogState(state);
        if (state == PeerConnectionState.Connected)
        {
            try
            {
                BuildStreams();
                _ = _connected.TrySetResult();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogStreamsFailed(exception);
                _ = _connected.TrySetException(exception);
            }
        }
        else if (state is PeerConnectionState.Failed or PeerConnectionState.Closed)
        {
            _ = _connected.TrySetException(
                new InvalidOperationException(
                    $"The peer connection {state.ToString().ToLowerInvariant()} before media could flow."
                )
            );
        }

        StateChanged?.Invoke(state);
    }

    // Builds the streams from the negotiated media, once, when the connection first connects. Each media
    // stands alone: a source or sink that cannot start (a machine with no microphone) loses its own media
    // and the others still flow; the session fails only when none of them could be built.
    private void BuildStreams()
    {
        PeerConnection connection = _connection!;
        lock (_gate)
        {
            if (_streamsBuilt)
            {
                return;
            }

            _streamsBuilt = true;
        }

        int built = 0;
        Exception? failure = null;
        foreach (NegotiatedMediaInfo media in connection.NegotiatedMedia)
        {
            if (media.Codecs.Count == 0)
            {
                continue;
            }

            SdpCodec local = media.Codecs[0];
            SdpCodec remote = media.RemoteCodecs[0];
            if (!_services.PayloadFormats.TryGet(local, out RtpPayloadFormat? payloadFormat))
            {
                LogUnknownCodec(local.EncodingName);
                continue;
            }

            LogNegotiated(media.Kind, local.EncodingName, local.PayloadType);
            try
            {
                if (media.Kind == SdpMediaKind.Video)
                {
                    BuildVideo(media, local, remote, payloadFormat);
                }
                else if (media.Kind == SdpMediaKind.Audio)
                {
                    BuildAudio(media, local, payloadFormat);
                }

                built++;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogMediaFailed(exception, media.Kind);
                failure ??= exception;
            }
        }

        if (built == 0 && failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }

    private void BuildVideo(
        NegotiatedMediaInfo media,
        SdpCodec local,
        SdpCodec remote,
        RtpPayloadFormat payloadFormat
    )
    {
        VideoCodecId codec = new(local.EncodingName);
        _videoPayloadType = local.PayloadType;
        if (_endpoints.VideoSource is { } source)
        {
            long start = _connection!.CurrentBitrateEstimate.TargetBitrateBps;
            _videoSend = new VideoSendStream(
                source,
                new VideoSendSetup(
                    new VideoCodecFormat(codec, CodecParameters(local.FormatParameters)),
                    Writer(payloadFormat, local, media.LocalSsrc),
                    start > 0 ? start : DefaultStartBitsPerSecond
                ),
                _services.Codecs,
                _options,
                _pacer!,
                _logger
            );
        }

        if (_endpoints.VideoSink is { } sink)
        {
            bool alpha =
                FormatParameters
                    .Parse(remote.FormatParameters)
                    .TryGetValue(FormatParameters.AlphaName, out string? layout)
                && string.Equals(
                    layout,
                    FormatParameters.SideBySide,
                    StringComparison.OrdinalIgnoreCase
                );
            _videoReceive = new VideoReceiveStream(
                new VideoReceiveSetup(
                    new VideoCodecFormat(codec, CodecParameters(remote.FormatParameters)),
                    payloadFormat,
                    alpha ? AlphaLayout.PackSideBySide : AlphaLayout.None,
                    RequestKeyframeAsync
                ),
                sink,
                _services.Codecs,
                _playout,
                _services.Clock,
                _logger
            );
        }
    }

    private void BuildAudio(
        NegotiatedMediaInfo media,
        SdpCodec local,
        RtpPayloadFormat payloadFormat
    )
    {
        AudioCodecFormat format = new(
            new AudioCodecId(local.EncodingName),
            CodecParameters(local.FormatParameters)
        );
        _audioPayloadType = local.PayloadType;
        if (_endpoints.AudioSource is { } source)
        {
            _audioSend = new AudioSendStream(
                source,
                format,
                Writer(payloadFormat, local, media.LocalSsrc),
                _services.Codecs,
                _options,
                _pacer!
            );
        }

        if (_endpoints.AudioSink is { } sink)
        {
            _audioReceive = new AudioReceiveStream(
                format,
                payloadFormat,
                sink,
                _services.Codecs,
                _playout,
                _services.Clock,
                _logger
            );
        }
    }

    private RtpStreamWriter Writer(RtpPayloadFormat format, SdpCodec codec, uint ssrc) =>
        new(
            format.CreatePacketizer(MaxPayloadSize),
            (byte)codec.PayloadType,
            ssrc,
            new ClockRate(format.ClockRate),
            _captureClock
        );

    // The codec's own parameters, without the ones this library adds about the stream.
    private static ImmutableSortedDictionary<string, string> CodecParameters(string? fmtp) =>
        FormatParameters.Parse(fmtp).Remove(FormatParameters.AlphaName);

    private void OnRtp(RtpHeader header, ReadOnlyMemory<byte> payload)
    {
        if (header.PayloadType == _videoPayloadType && _videoReceive is { } video)
        {
            _remoteVideoSsrc = header.Ssrc;
            video.OnPacket(in header, payload.Span);
        }
        else if (header.PayloadType == _audioPayloadType && _audioReceive is { } audio)
        {
            audio.OnPacket(in header, payload.Span);
        }
    }

    private void OnBitrateEstimate(BitrateEstimate estimate)
    {
        long audio = _audioSend is null ? 0 : _options.AudioBitsPerSecond;
        _videoSend?.SetBitrate(
            Math.Max(estimate.TargetBitrateBps - audio, MinimumVideoBitsPerSecond)
        );
        _pacer?.SetRate(estimate.PacingRateBps);
    }

    private ValueTask SendAsync(PacedPacket packet, CancellationToken cancellationToken) =>
        _connection!.SendRtp(
            packet.PayloadType,
            packet.Ssrc,
            packet.Timestamp,
            packet.Marker,
            packet.Buffer.AsMemory(0, packet.Length),
            packet.CaptureNtp,
            cancellationToken
        );

    private async ValueTask RequestKeyframeAsync()
    {
        if (_connection is { } connection && _remoteVideoSsrc != 0)
        {
            await connection.RequestKeyframeAsync(_remoteVideoSsrc).ConfigureAwait(false);
        }
    }

    private async Task OnDescriptionAsync(SessionDescription description)
    {
        PeerConnection connection = _connection!;
        if (!SdpReader.TryParse(description.Sdp, out SdpDescription parsed))
        {
            LogUnparsableDescription(description.Kind);
            return;
        }

        try
        {
            connection.SetRemoteDescription(
                parsed,
                description.Kind == SdpKind.Offer ? SdpType.Offer : SdpType.Answer
            );
            if (description.Kind == SdpKind.Offer)
            {
                await SendDescriptionAsync(SdpKind.Answer, connection.CreateAnswer(), default)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogNegotiationFailed(exception, description.Kind);
            _ = _connected.TrySetException(exception);
        }
    }

    private Task OnCandidateAsync(IceCandidate candidate)
    {
        if (WebRtcIceCandidate.TryParse(candidate.Candidate, out WebRtcIceCandidate parsed))
        {
            _connection!.AddRemoteIceCandidate(parsed);
        }
        else
        {
            LogUnparsableCandidate(candidate.Candidate);
        }

        return Task.CompletedTask;
    }

    private void OnLocalCandidate(WebRtcIceCandidate candidate) =>
        _ = SendCandidateAsync(candidate);

    private async Task SendCandidateAsync(WebRtcIceCandidate candidate)
    {
        try
        {
            await _signaling
                .SendAsync(
                    new IceCandidate(
                        candidate.ToSdp(),
                        candidate.ComponentId.ToString(CultureInfo.InvariantCulture),
                        0
                    )
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Other candidates, or those gathered later, can still connect the session.
            LogCandidateSendFailed(exception);
        }
    }

    private async Task SendDescriptionAsync(
        SdpKind kind,
        SdpDescription description,
        CancellationToken cancellationToken
    )
    {
        await _signaling
            .SendAsync(
                new SessionDescription(kind, SdpWriter.Write(description)),
                cancellationToken
            )
            .ConfigureAwait(false);
        LogDescriptionSent(kind);
    }

    private static uint NewSsrc() =>
        unchecked((uint)RandomNumberGenerator.GetInt32(1, int.MaxValue) * 2 + 1);

    [LoggerMessage(
        2100,
        LogLevel.Information,
        "Media session starting as {Role} (video {Video}, audio {Audio})."
    )]
    private partial void LogStarting(MediaSessionRole role, bool video, bool audio);

    [LoggerMessage(2101, LogLevel.Debug, "Peer connection {State}.")]
    private partial void LogState(PeerConnectionState state);

    [LoggerMessage(
        2102,
        LogLevel.Information,
        "Negotiated {Kind} codec {Codec} (payload type {PayloadType})."
    )]
    private partial void LogNegotiated(SdpMediaKind kind, string codec, int payloadType);

    [LoggerMessage(
        2103,
        LogLevel.Warning,
        "The negotiated codec {Codec} has no registered payload format; its media is not used."
    )]
    private partial void LogUnknownCodec(string codec);

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
    private partial void LogMediaFailed(Exception exception, SdpMediaKind kind);

    [LoggerMessage(2105, LogLevel.Warning, "Discarded an unparsable remote {Kind}.")]
    private partial void LogUnparsableDescription(SdpKind kind);

    [LoggerMessage(2106, LogLevel.Error, "Applying the remote {Kind} failed.")]
    private partial void LogNegotiationFailed(Exception exception, SdpKind kind);

    [LoggerMessage(
        2107,
        LogLevel.Debug,
        "Discarded an unparsable remote ICE candidate: {Candidate}."
    )]
    private partial void LogUnparsableCandidate(string candidate);

    [LoggerMessage(2108, LogLevel.Warning, "Sending a local ICE candidate failed.")]
    private partial void LogCandidateSendFailed(Exception exception);

    [LoggerMessage(2109, LogLevel.Debug, "Sent the local {Kind}.")]
    private partial void LogDescriptionSent(SdpKind kind);

    [LoggerMessage(2110, LogLevel.Warning, "The STUN server {Url} did not resolve; it is skipped.")]
    private partial void LogStunUnresolved(string url);

    [LoggerMessage(2111, LogLevel.Information, "Media session stopped.")]
    private partial void LogStopped();
}
