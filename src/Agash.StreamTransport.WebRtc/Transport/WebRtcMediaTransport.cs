using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.Media;
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
    private const int MaxPayloadSize = 1100;
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
                connection.CircuitBreakerState
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
        (List<IPEndPoint> stun, List<string> unresolved) = await StunServers
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

    public bool TrySendVideo(in EncodedVideoFrame frame, NtpTime capture) =>
        _videoWriter is { } writer
        && _connection is { } connection
        && writer.Write(connection, frame.Data, frame.Timestamp, capture);

    public bool TrySendAudio(in EncodedAudioFrame frame, NtpTime capture) =>
        _audioWriter is { } writer
        && _connection is { } connection
        && writer.Write(connection, frame.Data, frame.Timestamp, capture);

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
        List<IPEndPoint> stun,
        List<TurnServer> turn
    )
    {
        List<MediaLine> lines = [];
        if (offer.Audio is { } audio)
        {
            List<SdpCodec> codecs = [];
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
                new MediaLine("0", SdpMediaKind.Audio, NewSsrc(), codecs)
                {
                    Direction = Direction(audio.Sends, audio.Receives),
                }
            );
        }

        if (offer.Video is { } video)
        {
            List<SdpCodec> codecs = [];
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

            lines.Add(
                new MediaLine("1", SdpMediaKind.Video, NewSsrc(), codecs)
                {
                    RtxSsrc = NewSsrc(),
                    Direction = Direction(video.Sends, video.Receives),
                }
            );
        }

        return new PeerConnectionOptions
        {
            Media = lines,
            PayloadFormats = _services.PayloadFormats,
            StunServers = stun,
            TurnServers = turn,
            IceTransportPolicy = _options.IceTransportPolicy,
            IncludeLoopback = _options.IncludeLoopbackCandidates,
            LocalAddressPreferences = _options.LocalAddressPreferences,
            EnableFec = _options.ForwardErrorCorrection && offer.Video?.Sends == true,
            FecProtectedSsrc =
                lines.FirstOrDefault(l => l.Kind == SdpMediaKind.Video)?.LocalSsrc ?? 0,
        };
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
            if (media.Kind == SdpMediaKind.Video && offer.Video is { } videoOffer)
            {
                VideoCodecId codec = new(local.EncodingName);
                ImmutableArray<AlphaLayout> ours = FormatParameters.AlphaLayouts(
                    local.FormatParameters
                );
                ImmutableArray<AlphaLayout> theirs = FormatParameters.AlphaLayouts(
                    remote.FormatParameters
                );
                AlphaLayout alpha =
                    _role == MediaSessionRole.Answerer
                        ? FormatParameters.ChooseAlpha(theirs, ours)
                        : FormatParameters.ChooseAlpha(ours, theirs);
                video = new NegotiatedVideo(
                    new VideoCodecFormat(codec, SendParameters(codec, remote.FormatParameters)),
                    new VideoCodecFormat(codec, CodecParameters(remote.FormatParameters)),
                    alpha
                );
                _videoPayloadType = local.PayloadType;
                if (videoOffer.Sends)
                {
                    _videoWriter = Writer(payloadFormat, local, media.LocalSsrc);
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
                    )
                );
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

        return new NegotiatedMedia(video, audio);
    }

    private static RtpStreamWriter Writer(RtpPayloadFormat format, SdpCodec codec, uint ssrc) =>
        new(
            format.CreatePacketizer(MaxPayloadSize),
            (byte)codec.PayloadType,
            ssrc,
            new ClockRate(format.ClockRate)
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
            // The end of the peer's candidates: nothing to add.
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

    // A channel that does not trickle gets the candidates inside the description instead.
    private void OnLocalCandidate(WebRtcIceCandidate candidate)
    {
        if (_signaling.SupportsTrickle)
        {
            _ = SendCandidateAsync(candidate);
        }
    }

    private async Task SendCandidateAsync(WebRtcIceCandidate candidate)
    {
        try
        {
            await _signaling
                .SendAsync(
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
            description = connection.WithLocalCandidates(description);
        }

        await _signaling
            .SendAsync(
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

    private static uint NewSsrc() =>
        unchecked((uint)RandomNumberGenerator.GetInt32(1, int.MaxValue) * 2 + 1);

    // Received video: RTP into a frame buffer that reorders it and hands over complete frames, asking for a
    // keyframe when a frame cannot be completed or a gap outlasts the time NACK and RTX get to fill it.
    // Runs on the connection's receive thread.
    private sealed class VideoAssembler(WebRtcMediaTransport owner, RtpPayloadFormat format)
        : IDisposable
    {
        private readonly RtpFrameBuffer _buffer = new(format);
        private readonly RtpClockAligner _aligner = new(new ClockRate(format.ClockRate));
        private bool _captureTimes;
        private long? _gapSince;

        public void OnPacket(in RtpHeader header, ReadOnlySpan<byte> payload)
        {
            if (header.AbsoluteCaptureTimeNtp is { } ntp and not 0)
            {
                _captureTimes = true;
                _aligner.Record(new NtpTime(ntp), header.Timestamp);
            }

            RtpFrameBuffer.InsertResult result = _buffer.Insert(
                header.SequenceNumber,
                header.Timestamp,
                header.Marker,
                payload
            );
            foreach (RtpFrameBuffer.AssembledFrame frame in result.Frames)
            {
                NtpTime? capture = _aligner.TryGetCapture(frame.Timestamp, out NtpTime at)
                    ? at
                    : null;
                owner._receiver!.OnVideoFrame(frame.Frame, frame.IsKeyframe, capture);
            }

            long now = owner._time.GetTimestamp();
            bool required = result.KeyframeRequired;
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
                capture
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

    [LoggerMessage(1168, LogLevel.Warning, "The STUN server {Url} did not resolve; it is skipped.")]
    private partial void LogStunUnresolved(string url);

    [LoggerMessage(1169, LogLevel.Debug, "A keyframe request could not be sent.")]
    private partial void LogKeyframeRequestFailed(Exception exception);
}
