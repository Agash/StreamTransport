using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.WebRtc.Ice;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;
using Agash.StreamTransport.WebRtc.Sdp;
using Agash.StreamTransport.WebRtc.Turn;
using Microsoft.Extensions.Logging;
using WebRtcIceCandidate = Agash.StreamTransport.WebRtc.Ice.IceCandidate;

namespace Agash.StreamTransport.WebRtc.Transport;

/// <summary>What every WebRTC transport of a host shares.</summary>
/// <param name="Connections">Makes peer connections.</param>
/// <param name="PayloadFormats">The registered RTP payload formats.</param>
/// <param name="Loggers">The logging.</param>
/// <param name="Mobility">Re-probes connections when the host's networks change; null for none.</param>
internal sealed record WebRtcTransportServices(
    PeerConnectionFactory Connections,
    RtpPayloadFormatRegistry PayloadFormats,
    ILoggerFactory Loggers,
    MobilityEngine? Mobility
);

/// <summary>
/// A session's media over one WebRTC peer connection: negotiates the offer as SDP over the signaling
/// channel, packetizes sent frames as RTP, and assembles received RTP back into frames with the sender's
/// capture time, from abs-capture-time or else the sender reports.
/// </summary>
internal sealed partial class WebRtcMediaTransport : IMediaTransport
{
    // RTP payload size that fits a 1280-byte IPv6 minimum MTU after IP, UDP, RTP, extension and SRTP.
    private const int FirstVideoPayloadType = 96;
    private const int AudioPayloadType = 111;

    // At most one keyframe request per interval, so a burst of loss is not a burst of requests.
    private static readonly TimeSpan KeyframeRequestInterval = TimeSpan.FromMilliseconds(250);

    // How long a sequence gap may wait for NACK and RTX to fill it before a keyframe is requested.
    private static readonly TimeSpan GapPatience = TimeSpan.FromMilliseconds(100);

    // How long a description for a non-trickle peer waits for STUN and TURN servers to answer.
    private static readonly TimeSpan GatherLimit = TimeSpan.FromSeconds(3);

    private readonly ISignalingChannel _signaling;
    private readonly MediaSessionRole _role;
    private readonly WebRtcTransportOptions _options;
    private readonly WebRtcTransportServices _services;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly TaskCompletionSource<NegotiatedMedia> _connected = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private PeerConnection? _connection;
    private IReceivedMediaConsumer? _receiver;
    private MediaOffer? _offer;
    private IDisposable? _mobility;
    private RtpStreamWriter? _videoWriter;
    private RtpStreamWriter? _audioWriter;
    private VideoAssembler? _videoReceive;
    private AudioReceiver? _audioReceive;
    private int _videoPayloadType = -1;
    private int _audioPayloadType = -1;
    private uint _remoteVideoSsrc;
    private long _lastKeyframeRequest;
    private int _started;
    private int _disposed;

    public WebRtcMediaTransport(
        ISignalingChannel signaling,
        MediaSessionRole role,
        WebRtcTransportOptions options,
        WebRtcTransportServices services
    )
    {
        _signaling = signaling;
        _role = role;
        _options = options;
        _services = services;
        _logger = services.Loggers.CreateLogger<WebRtcMediaTransport>();
        _time = services.Connections.TimeProvider;
        _lastKeyframeRequest =
            _time.GetTimestamp()
            - (long)(KeyframeRequestInterval.TotalSeconds * _time.TimestampFrequency);
    }

    public event Action<TransportState>? StateChanged;

    public event Action<CapacityEstimate>? CapacityChanged;

    public event Action? KeyframeRequested;

    public event Action<CircuitBreakerState>? CircuitBreakerChanged;

    public TransportState State =>
        _connection?.State switch
        {
            null or PeerConnectionState.New => TransportState.New,
            PeerConnectionState.Connecting => TransportState.Connecting,
            PeerConnectionState.Connected => TransportState.Connected,
            PeerConnectionState.Disconnected => TransportState.Disconnected,
            PeerConnectionState.Failed => TransportState.Failed,
            _ => TransportState.Closed,
        };

    public CapacityEstimate Capacity => _connection?.CurrentCapacity ?? default;

    public MediaRoute? Route =>
        _connection?.SelectedPath is { } path
            ? new MediaRoute(path.Local, path.Remote, path.IsRelayed)
            : null;

    public TransportStatistics Statistics
    {
        get
        {
            if (_connection is not { } connection)
            {
                return default;
            }

            TransportLossStats loss = connection.CurrentLossStats;
            return new TransportStatistics(
                connection.CurrentCapacity,
                connection.CurrentHealth.LossRate,
                loss.MediaPacketsSent,
                loss.RtxPacketsSent,
                loss.NackSequencesRequested,
                loss.RtxPacketsRecovered,
                loss.KeyframeRequestsSent,
                connection.CircuitBreakerState,
                connection.SenderClockOffset
            );
        }
    }

    public async Task<NegotiatedMedia> ConnectAsync(
        MediaOffer offer,
        IReceivedMediaConsumer receiver,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(receiver);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The transport is already connecting.");
        }

        _offer = offer;
        _receiver = receiver;
        (ImmutableArray<IPEndPoint> stun, ImmutableArray<string> unresolved) = await StunServers
            .ResolveAsync(_options.IceServers, cancellationToken)
            .ConfigureAwait(false);
        foreach (string url in unresolved)
        {
            LogStunUnresolved(url);
        }

        PeerConnection connection = _services.Connections.Create(
            BuildOptions(offer, stun, StunServers.Turn(_options.IceServers))
        );
        _connection = connection;
        connection.LocalIceCandidate += OnLocalCandidate;
        connection.StateChanged += OnStateChanged;
        connection.ConnectivityLost += OnConnectivityLost;
        connection.RtpReceived += OnRtp;
        connection.SenderReportReceived += OnSenderReport;
        connection.KeyframeRequested += _ => KeyframeRequested?.Invoke();
        connection.CapacityChanged += estimate => CapacityChanged?.Invoke(estimate);
        connection.CircuitBreakerChanged += (state, _) => CircuitBreakerChanged?.Invoke(state);
        _signaling.DescriptionReceived += OnDescriptionAsync;
        _signaling.IceCandidateReceived += OnCandidateAsync;
        _mobility = _services.Mobility?.Register(connection.TriggerNetworkRecovery);

        if (_role == MediaSessionRole.Offerer)
        {
            await SendDescriptionAsync(SdpKind.Offer, connection.CreateOffer(), cancellationToken)
                .ConfigureAwait(false);
        }

        using CancellationTokenRegistration cancel = cancellationToken.Register(
            static state => ((TaskCompletionSource<NegotiatedMedia>)state!).TrySetCanceled(),
            _connected
        );
        return await _connected.Task.ConfigureAwait(false);
    }

    public long SentBytes(TrafficClass trafficClass) => _connection?.SentBytes(trafficClass) ?? 0;

    public bool TrySendVideo(in EncodedVideoFrame frame, NtpTime? capture, FrameSendTiming? timing)
    {
        if (_videoWriter is not { } writer || _connection is not { } connection)
        {
            return false;
        }

        connection.NoteMediaFrame(frame.Data.Length);
        return writer.Write(
            connection,
            frame.Data,
            frame.Timestamp,
            capture,
            timing,
            orientation: MediaExtensionValues.ToCvo(frame.Orientation),
            keyframe: frame.Keyframe
        );
    }

    public bool TrySendAudio(in EncodedAudioFrame frame, NtpTime? capture) =>
        _audioWriter is { } writer
        && _connection is { } connection
        && writer.Write(
            connection,
            frame.Data,
            frame.Timestamp,
            capture,
            audioLevel: frame.Level is { } level ? MediaExtensionValues.ToAudioLevel(level) : null
        );

    public void RequestKeyframe()
    {
        long now = _time.GetTimestamp();
        long last = Volatile.Read(ref _lastKeyframeRequest);
        if (
            _connection is not { } connection
            || _remoteVideoSsrc == 0
            || _time.GetElapsedTime(last, now) < KeyframeRequestInterval
            || Interlocked.CompareExchange(ref _lastKeyframeRequest, now, last) != last
        )
        {
            return;
        }

        _ = SendKeyframeRequestAsync(connection, _remoteVideoSsrc);
    }

    public bool TryResume() => _connection?.TryResumeTransmission() ?? true;

    public ValueTask<bool> RequestReceiveLimitAsync(
        long? bitsPerSecond,
        CancellationToken cancellationToken = default
    ) =>
        _connection is { } connection && _remoteVideoSsrc != 0
            ? connection.RequestSendLimitAsync(_remoteVideoSsrc, bitsPerSecond, cancellationToken)
            : ValueTask.FromResult(false);

    // An ICE restart (RFC 8829): fresh credentials and candidates in a new offer; the answer brings the
    // peer's, and media carries on under the same DTLS-SRTP keys.
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not { } connection)
        {
            return;
        }

        LogIceRestart();
        await SendDescriptionAsync(SdpKind.Offer, connection.RestartIce(), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _signaling.DescriptionReceived -= OnDescriptionAsync;
        _signaling.IceCandidateReceived -= OnCandidateAsync;
        _mobility?.Dispose();
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _videoReceive?.Dispose();
        _ = _connected.TrySetCanceled();
    }

    private async Task SendKeyframeRequestAsync(PeerConnection connection, uint ssrc)
    {
        try
        {
            await connection.RequestKeyframeAsync(ssrc).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The next decode failure asks again.
            LogKeyframeRequestFailed(exception);
        }
    }

    // The media lines: each kind offered, with the codecs a payload format exists for, in the offer's order.
    private PeerConnectionOptions BuildOptions(
        MediaOffer offer,
        ImmutableArray<IPEndPoint> stun,
        ImmutableArray<TurnServer> turn
    )
    {
        ImmutableArray<MediaLine>.Builder lines = ImmutableArray.CreateBuilder<MediaLine>(2);
        if (offer.Audio is { } audio)
        {
            ImmutableArray<SdpCodec>.Builder codecs = ImmutableArray.CreateBuilder<SdpCodec>();
            int payloadType = AudioPayloadType;
            foreach (AudioCodecFormat codec in audio.Codecs)
            {
                if (
                    _services.PayloadFormats.TryGet(codec.Codec.Name, out RtpPayloadFormat? format)
                    && format.Kind == SdpMediaKind.Audio
                )
                {
                    foreach (string? parameters in format.FormatParameterSets)
                    {
                        codecs.Add(format.ToSdpCodec(payloadType++, parameters));
                    }
                }
            }

            lines.Add(
                new MediaLine("0", SdpMediaKind.Audio, NewSsrc(), codecs.DrainToImmutable())
                {
                    Direction = Direction(audio.Sends, audio.Receives),
                    MaximumReceiveBitsPerSecond = audio.MaximumReceiveBitsPerSecond,
                }
            );
        }

        if (offer.Video is { } video)
        {
            ImmutableArray<SdpCodec>.Builder codecs = ImmutableArray.CreateBuilder<SdpCodec>();
            int payloadType = FirstVideoPayloadType;
            foreach (VideoCodecOffer codec in video.Codecs)
            {
                if (
                    !_services.PayloadFormats.TryGet(
                        codec.Format.Codec.Name,
                        out RtpPayloadFormat? format
                    )
                    || format.Kind != SdpMediaKind.Video
                )
                {
                    continue;
                }

                foreach (string? parameters in format.FormatParameterSets)
                {
                    var offered = format.ToSdpCodec(payloadType++, parameters);
                    if (!codec.Alpha.IsEmpty)
                    {
                        offered = offered with
                        {
                            FormatParameters = FormatParameters.Append(
                                offered.FormatParameters,
                                FormatParameters.AlphaName,
                                FormatParameters.AlphaValue(codec.Alpha)
                            ),
                        };
                    }

                    codecs.Add(offered);

                    // Its retransmissions, on a payload type of their own (RFC 4588).
                    codecs.Add(Rtx.For(payloadType++, offered.PayloadType, offered.ClockRate));
                }
            }

            // FlexFEC (RFC 8627): received whenever the peer sends it; sent when asked for.
            codecs.Add(FlexFec.Codec(payloadType++));
            lines.Add(
                new MediaLine("1", SdpMediaKind.Video, NewSsrc(), codecs.DrainToImmutable())
                {
                    RtxSsrc = NewSsrc(),
                    FecSsrc = _options.ForwardErrorCorrection && video.Sends ? NewSsrc() : null,
                    Direction = Direction(video.Sends, video.Receives),
                    MaximumReceiveBitsPerSecond = video.MaximumReceiveBitsPerSecond,
                }
            );
        }

        return new PeerConnectionOptions
        {
            Media = lines.DrainToImmutable(),
            PayloadFormats = _services.PayloadFormats,
            StunServers = stun,
            TurnServers = turn,
            IceTransportPolicy = _options.IceTransportPolicy,
            IncludeLoopback = _options.IncludeLoopbackCandidates,
            LocalAddressPreferences = _options.LocalAddressPreferences,
            SocketFactory = _options.SocketFactory,
            RepairOverStandby = _options.RepairOverSecondPath,
            IceTimings = IceTimingsFor(_options),
            PlayoutBudget = _options.RequestedPlayoutDelay?.Maximum,
        };
    }

    // ICE's timings: as given, or libwebrtc's with the receiving timeout set to the path failover time and a
    // standby path checked at twice that, so the path media moves to was heard from recently.
    internal static IceTimings? IceTimingsFor(WebRtcTransportOptions options) =>
        options.IceTimings
        ?? (
            options.PathFailover is { } failover
                ? IceTimings.Default with
                {
                    ReceivingTimeout = failover,
                    StandbyPingInterval =
                        failover * 2 > TimeSpan.FromSeconds(1)
                            ? failover * 2
                            : TimeSpan.FromSeconds(1),
                }
                : null
        );

    // The path was lost with its credentials spent: the offering side restarts ICE, which the signaling
    // carries to the peer (a WHIP client by PATCH); the answering side waits for that restart.
    private void OnConnectivityLost()
    {
        if (_role == MediaSessionRole.Offerer && Volatile.Read(ref _disposed) == 0)
        {
            _ = RestartAfterLossAsync();
        }
    }

    private async Task RestartAfterLossAsync()
    {
        try
        {
            await RestartAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogRestartFailed(exception);
        }
    }

    private void OnStateChanged(PeerConnectionState state)
    {
        LogState(state);
        if (state == PeerConnectionState.Connected && !_connected.Task.IsCompleted)
        {
            try
            {
                _ = _connected.TrySetResult(Negotiate());
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogNegotiationFailed(exception, SdpKind.Answer);
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

        StateChanged?.Invoke(State);
    }

    // What was agreed, with the writers and assemblers for it.
    private NegotiatedMedia Negotiate()
    {
        PeerConnection connection = _connection!;
        MediaOffer offer = _offer!;
        NegotiatedVideo? video = null;
        NegotiatedAudio? audio = null;
        NegotiatedMediaInfo? videoSection = null;
        NegotiatedMediaInfo? audioSection = null;
        foreach (NegotiatedMediaInfo media in connection.NegotiatedMedia)
        {
            // The first media codec; retransmission and FEC codecs repair it and are not candidates.
            int first = media
                .Codecs.ToList()
                .FindIndex(static c => !Rtx.IsRtx(c) && !FlexFec.IsFlexFec(c));
            if (first < 0)
            {
                continue;
            }

            SdpCodec local = media.Codecs[first];
            SdpCodec remote = media.RemoteCodecs[first];
            if (!_services.PayloadFormats.TryGet(local, out RtpPayloadFormat? payloadFormat))
            {
                LogUnknownCodec(local.EncodingName);
                continue;
            }

            LogNegotiated(media.Kind, local.EncodingName, local.PayloadType);
            if (media.Kind == SdpMediaKind.Video && offer.Video is { } videoOffer)
            {
                VideoCodecId codec = new(local.EncodingName);
                ImmutableArray<AlphaLayout> ours = FormatParameters.AlphaLayouts(
                    local.FormatParameters
                );
                ImmutableArray<AlphaLayout> theirs = FormatParameters.AlphaLayouts(
                    remote.FormatParameters
                );
                // The sender orders the ways, whether it offered or answered.
                AlphaLayout alpha = videoOffer.Sends
                    ? FormatParameters.ChooseAlpha(ours, theirs)
                    : FormatParameters.ChooseAlpha(theirs, ours);
                video = new NegotiatedVideo(
                    new VideoCodecFormat(codec, SendParameters(codec, remote.FormatParameters)),
                    new VideoCodecFormat(codec, CodecParameters(remote.FormatParameters)),
                    alpha,
                    media.SendLimitBitsPerSecond,
                    media.RemoteTrack
                );
                videoSection = media;
                _videoPayloadType = local.PayloadType;
                if (videoOffer.Sends)
                {
                    _videoWriter = Writer(
                        payloadFormat,
                        local,
                        media.LocalSsrc,
                        _options.RequestedPlayoutDelay is { } asked
                            ? new PlayoutDelay(asked.Minimum, asked.Maximum)
                            : null
                    );
                }

                if (videoOffer.Receives)
                {
                    _videoReceive = new VideoAssembler(this, payloadFormat);
                }
            }
            else if (media.Kind == SdpMediaKind.Audio && offer.Audio is { } audioOffer)
            {
                audio = new NegotiatedAudio(
                    new AudioCodecFormat(
                        new AudioCodecId(local.EncodingName),
                        CodecParameters(local.FormatParameters)
                    ),
                    media.SendLimitBitsPerSecond,
                    media.RemoteTrack
                );
                audioSection = media;
                _audioPayloadType = local.PayloadType;
                if (audioOffer.Sends)
                {
                    _audioWriter = Writer(payloadFormat, local, media.LocalSsrc);
                }

                if (audioOffer.Receives)
                {
                    _audioReceive = new AudioReceiver(this, new ClockRate(payloadFormat.ClockRate));
                }
            }
        }

        return new NegotiatedMedia(video, audio, Synchronized(videoSection, audioSection));
    }

    // RFC 3550 section 6.5.1: sources are synchronized only under one CNAME. RFC 8830 section 1: tracks
    // the peer puts in one MediaStream play out in sync, and tracks it puts in different ones, or in
    // none, need not.
    internal static bool Synchronized(NegotiatedMediaInfo? video, NegotiatedMediaInfo? audio)
    {
        if (video is null || audio is null)
        {
            return true;
        }

        if (
            video.RemoteCname is { } videoName
            && audio.RemoteCname is { } audioName
            && videoName != audioName
        )
        {
            return false;
        }

        return video.RemoteTrack is not { } videoTrack
            || audio.RemoteTrack is not { } audioTrack
            || (videoTrack.StreamId is { } stream && stream == audioTrack.StreamId);
    }

    private static RtpStreamWriter Writer(
        RtpPayloadFormat format,
        SdpCodec codec,
        uint ssrc,
        PlayoutDelay? playoutDelay = null
    ) =>
        new(
            format.CreatePacketizer(),
            (byte)codec.PayloadType,
            ssrc,
            new ClockRate(format.ClockRate),
            playoutDelay
        );

    // What the receiver declared, for the encoder: an H.264 peer that names no profile takes Baseline
    // (RFC 6184), which the encoder must then keep to.
    private static ImmutableSortedDictionary<string, string> SendParameters(
        VideoCodecId codec,
        string? fmtp
    )
    {
        ImmutableSortedDictionary<string, string> parameters = CodecParameters(fmtp);
        return
            codec == VideoCodecId.H264 && !parameters.ContainsKey(H264ProfileLevelId.ParameterName)
            ? parameters.Add(
                H264ProfileLevelId.ParameterName,
                H264ProfileLevelId.Default.ToString()
            )
            : parameters;
    }

    // The codec's own parameters, without the ones this library adds about the stream.
    private static ImmutableSortedDictionary<string, string> CodecParameters(string? fmtp) =>
        FormatParameters.Parse(fmtp).Remove(FormatParameters.AlphaName);

    // Whether the description carrying every candidate gathered so far has gone out.
    private bool _describedCandidates;

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

    // A sender report maps the stream's RTP clock to the sender's wall clock: the capture-time anchor
    // when the packets carry no abs-capture-time (as from a browser).
    private void OnSenderReport(uint ssrc, ulong ntp, uint rtpTimestamp)
    {
        if (ssrc == _remoteVideoSsrc)
        {
            _videoReceive?.OnSenderReport(new NtpTime(ntp), rtpTimestamp);
        }
        else
        {
            _audioReceive?.OnSenderReport(ssrc, new NtpTime(ntp), rtpTimestamp);
        }
    }

    private async Task OnDescriptionAsync(SessionDescription description)
    {
        if (_connection is not { } connection)
        {
            return;
        }

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

    private Task OnCandidateAsync(IceCandidateInit candidate)
    {
        if (string.IsNullOrEmpty(candidate.Candidate))
        {
            // The end of the peer's candidates (RFC 8838 section 14).
            _connection?.EndRemoteIceCandidates();
            return Task.CompletedTask;
        }

        if (WebRtcIceCandidate.TryParse(candidate.Candidate, out WebRtcIceCandidate parsed))
        {
            _connection?.AddRemoteIceCandidate(parsed);
        }
        else
        {
            LogUnparsableCandidate(candidate.Candidate);
        }

        return Task.CompletedTask;
    }

    // A channel that does not trickle gets the candidates inside the description instead; one gathered
    // after the description went out (an interface that came up mid-session) is sent on its own, and a
    // channel that cannot deliver it drops it.
    private void OnLocalCandidate(WebRtcIceCandidate candidate)
    {
        if (_signaling.SupportsTrickle || Volatile.Read(ref _describedCandidates))
        {
            _ = SendCandidateAsync(candidate);
        }
    }

    private async Task SendCandidateAsync(WebRtcIceCandidate candidate)
    {
        try
        {
            await _signaling
                .SendCandidateAsync(
                    new IceCandidateInit(
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
        if (!_signaling.SupportsTrickle && _connection is { } connection)
        {
            // One description carries every candidate to a peer that takes no others.
            await connection
                .WhenCandidatesGatheredAsync(GatherLimit, cancellationToken)
                .ConfigureAwait(false);

            // From here a new candidate is sent on its own; one gathered while the description is built
            // goes both ways, which the peer takes once.
            Volatile.Write(ref _describedCandidates, true);
            description = connection.WithLocalCandidates(description);
        }

        await _signaling
            .SendDescriptionAsync(
                new SessionDescription(kind, SdpWriter.Write(description)),
                cancellationToken
            )
            .ConfigureAwait(false);
        LogDescriptionSent(kind);
    }

    private static SdpDirection Direction(bool sends, bool receives) =>
        (sends, receives) switch
        {
            (true, false) => SdpDirection.SendOnly,
            (false, true) => SdpDirection.RecvOnly,
            _ => SdpDirection.SendRecv,
        };

    // RFC 3550 section 8.1: a random 32-bit identifier.
    private static uint NewSsrc() => SecureRandom.Ssrc();

    // Received video: RTP into a frame buffer that reorders it and hands over complete frames, asking for a
    // keyframe when a frame cannot be completed or a gap outlasts the time NACK and RTX get to fill it.
    // Runs on the connection's receive thread.
    private sealed class VideoAssembler(WebRtcMediaTransport owner, RtpPayloadFormat format)
        : IDisposable
    {
        private readonly RtpFrameBuffer _buffer = new(format);
        private readonly List<RtpFrameBuffer.AssembledFrame> _completed = [];
        private readonly RtpClockAligner _aligner = new(new ClockRate(format.ClockRate));

        // Frames still arriving, by RTP timestamp: when their first and last packets came, and the timing
        // the last one carried. A frame that never completes leaves its entry behind, cleared in bulk.
        private readonly Dictionary<uint, Arrival> _arrivals = [];
        private bool _captureTimes;
        private long? _gapSince;
        private PlayoutDelay? _playoutDelay;

        // TS 26.114 section 7.4.5: an orientation holds until the sender signals another.
        private VideoOrientation _orientation;

        public void OnPacket(in RtpHeader header, ReadOnlySpan<byte> payload)
        {
            var arrived = NtpTime.From(owner._time.GetUtcNow());
            if (_arrivals.Count > 64)
            {
                _arrivals.Clear();
            }

            _arrivals[header.Timestamp] = _arrivals.TryGetValue(header.Timestamp, out Arrival seen)
                ? seen with
                {
                    Last = arrived,
                    Timing = header.Extensions.VideoTiming ?? seen.Timing,
                }
                : new Arrival(arrived, arrived, header.Extensions.VideoTiming);

            if (header.AbsoluteCaptureTimeNtp is { } ntp and not 0)
            {
                _captureTimes = true;
                _aligner.Record(new NtpTime(ntp), header.Timestamp);
            }

            if (header.Extensions.VideoOrientation is { } cvo)
            {
                _orientation = MediaExtensionValues.FromCvo(cvo);
            }

            if (header.Extensions.PlayoutDelay is { } asked && asked != _playoutDelay)
            {
                _playoutDelay = asked;
                owner._receiver!.OnPlayoutDelayRequested(
                    new PlayoutDelayRange(asked.Minimum, asked.Maximum)
                );
            }

            _completed.Clear();
            bool required = _buffer.Insert(
                header.SequenceNumber,
                header.Timestamp,
                header.Marker,
                payload,
                _completed
            );
            foreach (RtpFrameBuffer.AssembledFrame frame in _completed)
            {
                NtpTime? capture = _aligner.TryGetCapture(frame.Timestamp, out NtpTime at)
                    ? at
                    : null;
                VideoReceiveTiming? timing =
                    _arrivals.Remove(frame.Timestamp, out Arrival arrival)
                    && arrival.Timing is { } sent
                        ? new VideoReceiveTiming(
                            (FrameTimingReasons)sent.Flags,
                            TimeSpan.FromMilliseconds(sent.EncodeStart),
                            TimeSpan.FromMilliseconds(sent.EncodeFinish),
                            TimeSpan.FromMilliseconds(sent.PacketizationFinish),
                            TimeSpan.FromMilliseconds(sent.PacerExit),
                            arrival.First,
                            arrival.Last
                        )
                        : null;
                owner._receiver!.OnVideoFrame(
                    frame.Frame,
                    frame.IsKeyframe,
                    capture,
                    timing,
                    _orientation
                );
            }

            long now = owner._time.GetTimestamp();
            if (_buffer.HasUnresolvedGap)
            {
                _gapSince ??= now;
                required |= owner._time.GetElapsedTime(_gapSince.Value, now) >= GapPatience;
            }
            else
            {
                _gapSince = null;
            }

            if (required)
            {
                _gapSince = null;
                owner.RequestKeyframe();
            }
        }

        public void OnSenderReport(NtpTime ntp, uint rtpTimestamp)
        {
            if (!_captureTimes)
            {
                _aligner.Record(ntp, rtpTimestamp);
            }
        }

        public void Dispose() => _buffer.Dispose();

        private readonly record struct Arrival(NtpTime First, NtpTime Last, VideoTiming? Timing);
    }

    // Received audio: each packet copied out of the receive buffer, numbered, placed on the stream's
    // timeline and stamped with its capture time.
    private sealed class AudioReceiver(WebRtcMediaTransport owner, ClockRate rate)
    {
        private readonly RtpClockAligner _aligner = new(rate);
        private bool _captureTimes;
        private uint _ssrc;
        private long _sequence = -1;
        private long _position = -1;
        private uint _lastTimestamp;

        public void OnPacket(in RtpHeader header, ReadOnlySpan<byte> payload)
        {
            _ssrc = header.Ssrc;
            if (header.AbsoluteCaptureTimeNtp is { } ntp and not 0)
            {
                _captureTimes = true;
                _aligner.Record(new NtpTime(ntp), header.Timestamp);
            }

            // Extended, so the stream's numbering and timeline never wrap.
            _sequence =
                _sequence < 0
                    ? header.SequenceNumber
                    : _sequence + (short)(ushort)(header.SequenceNumber - (ushort)_sequence);
            long position =
                _position < 0 ? 0 : _position + (int)(header.Timestamp - _lastTimestamp);
            if (_position < 0 || position > _position)
            {
                _position = position;
                _lastTimestamp = header.Timestamp;
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(payload.Length, 1));
            payload.CopyTo(buffer);
            NtpTime? capture = _aligner.TryGetCapture(header.Timestamp, out NtpTime at) ? at : null;
            owner._receiver!.OnAudioPacket(
                new EncodedFrameBuffer(buffer, payload.Length),
                _sequence,
                rate.ToTimeSpan(position),
                capture,
                header.Extensions.AudioLevel is { } level
                    ? MediaExtensionValues.FromAudioLevel(
                        level,
                        owner._connection?.PeerSignalsVoiceActivity ?? false
                    )
                    : null
            );
        }

        public void OnSenderReport(uint ssrc, NtpTime ntp, uint rtpTimestamp)
        {
            if (!_captureTimes && ssrc == _ssrc)
            {
                _aligner.Record(ntp, rtpTimestamp);
            }
        }
    }

    [LoggerMessage(1160, LogLevel.Debug, "Peer connection {State}.")]
    private partial void LogState(PeerConnectionState state);

    [LoggerMessage(
        1161,
        LogLevel.Information,
        "Negotiated {Kind} codec {Codec} (payload type {PayloadType})."
    )]
    private partial void LogNegotiated(SdpMediaKind kind, string codec, int payloadType);

    [LoggerMessage(
        1162,
        LogLevel.Warning,
        "The negotiated codec {Codec} has no registered payload format; its media is not used."
    )]
    private partial void LogUnknownCodec(string codec);

    [LoggerMessage(1163, LogLevel.Warning, "Discarded an unparsable remote {Kind}.")]
    private partial void LogUnparsableDescription(SdpKind kind);

    [LoggerMessage(1164, LogLevel.Error, "Applying the remote {Kind} failed.")]
    private partial void LogNegotiationFailed(Exception exception, SdpKind kind);

    [LoggerMessage(
        1165,
        LogLevel.Debug,
        "Discarded an unparsable remote ICE candidate: {Candidate}."
    )]
    private partial void LogUnparsableCandidate(string candidate);

    [LoggerMessage(1166, LogLevel.Warning, "Sending a local ICE candidate failed.")]
    private partial void LogCandidateSendFailed(Exception exception);

    [LoggerMessage(1167, LogLevel.Debug, "Sent the local {Kind}.")]
    private partial void LogDescriptionSent(SdpKind kind);

    [LoggerMessage(1173, LogLevel.Information, "Restarting ICE: fresh credentials and candidates.")]
    private partial void LogIceRestart();

    [LoggerMessage(
        1174,
        LogLevel.Warning,
        "The ICE restart after a lost path could not be offered."
    )]
    private partial void LogRestartFailed(Exception exception);

    [LoggerMessage(1168, LogLevel.Warning, "The STUN server {Url} did not resolve; it is skipped.")]
    private partial void LogStunUnresolved(string url);

    [LoggerMessage(1169, LogLevel.Debug, "A keyframe request could not be sent.")]
    private partial void LogKeyframeRequestFailed(Exception exception);
}
