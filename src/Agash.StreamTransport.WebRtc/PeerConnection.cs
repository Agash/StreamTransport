using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Security;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.WebRtc.Ice;
using Agash.StreamTransport.WebRtc.Rtcp;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;
using Agash.StreamTransport.WebRtc.Sdp;
using Agash.StreamTransport.WebRtc.Srtp;
using Agash.StreamTransport.WebRtc.Turn;
using Dtls.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// A point-to-point WebRTC peer connection: it composes the ICE agent, the DTLS-SRTP transport, and the
/// SRTP/RTP layers behind a JSEP-style offer/answer surface. One BUNDLE transport carries all media
/// (rtcp-mux). Media is paced and sent with <see cref="TrySendRtp"/> and surfaced via <see cref="RtpReceived"/>.
/// </summary>
public sealed partial class PeerConnection : IAsyncDisposable
{
    private readonly IMeterFactory? _meterFactory;
    private readonly WebRtcMetrics _metrics;
    private Activity? _connecting;
    private long _connectStarted;
    private int _connectSettled;
    private int _disposedForMetrics;
    private readonly PeerConnectionOptions _options;
    private readonly RtcCertificate _certificate;
    private readonly ILogger _logger;
    private IceCredentials _localIceCredentials = IceCredentials.Generate();
    private readonly List<IceCandidate> _bufferedRemoteCandidates = [];
    private readonly List<IceCandidate> _localCandidates = [];
    private readonly Dictionary<uint, ushort> _sendSequence = [];

    // UDP over IPv4 and the largest SRTP tag: what each packet costs the budget beyond itself.
    private static readonly int PacketOverhead = 28 + SrtpSession.MaxProtectionOverhead;
    private readonly Pacer _pacer;
    private readonly FrozenSet<uint> _audioSsrcs;
    private readonly ConcurrentDictionary<uint, RtpSendHistory> _sendHistory = new();

    // Retransmission as negotiated: replaced whole when a description is applied, read without a lock.
    private RtxConfiguration _rtx = RtxConfiguration.None;
    private readonly Lock _gate = new();
    private uint _rtcpSenderSsrc;

    private IceAgent? _iceAgent;
    private IceDatagramTransport? _dtlsTransport;
    private DtlsConnection? _dtls;
    private int _dtlsStarted;
    private SrtpSession? _srtp;
    private SdpDescription? _remoteDescription;
    private DtlsRole _dtlsRole = DtlsRole.Client;
    private DtlsFingerprint? _expectedRemoteFingerprint;
    private int _state = (int)PeerConnectionState.New;

    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _time;
    private readonly long _origin;

    /// <summary>Creates a peer connection.</summary>
    /// <param name="options">The media and ICE configuration.</param>
    /// <param name="certificate">The certificate its DTLS handshake authenticates with; not disposed by it.</param>
    /// <param name="loggerFactory">Optional logging.</param>
    /// <param name="controller">
    /// Optional send-side congestion controller. When supplied, inbound RFC 8888 feedback drives it and the
    /// resulting <see cref="CapacityChanged"/> estimates retune the encoder/pacer; when null, only
    /// receive-side feedback generation runs.
    /// </param>
    /// <param name="timeProvider">
    /// The clock behind ICE pacing and consent, congestion feedback and retransmission timing; the system's
    /// when null.
    /// </param>
    /// <param name="meterFactory">Where the metrics' meter comes from (<see cref="WebRtcDiagnostics.MeterName"/>); one shared meter when null.</param>
    public PeerConnection(
        PeerConnectionOptions options,
        RtcCertificate certificate,
        ILoggerFactory? loggerFactory = null,
        ICongestionController? controller = null,
        TimeProvider? timeProvider = null,
        IMeterFactory? meterFactory = null
    )
    {
        _options = options;
        _meterFactory = meterFactory;
        _metrics = WebRtcMetrics.For(meterFactory);
        _metrics.ConnectionsActive.Add(1);
        _time = timeProvider ?? TimeProvider.System;
        _origin = _time.GetTimestamp();
        ArgumentNullException.ThrowIfNull(certificate);
        _certificate = certificate;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<PeerConnection>();
        _controller = controller;

        foreach (MediaLine line in options.Media)
        {
            if (_rtcpSenderSsrc == 0)
            {
                _rtcpSenderSsrc = line.LocalSsrc;
            }
        }

        _audioSsrcs = options
            .Media.Where(static l => l.Kind == SdpMediaKind.Audio)
            .Select(static l => l.LocalSsrc)
            .ToFrozenSet();
        _pacer = new Pacer(
            TransmitAsync,
            PacketOverhead,
            _time,
            _loggerFactory.CreateLogger<Pacer>()
        )
        {
            BitsPerSecond = Math.Max(0, controller?.Current.PacingBitsPerSecond ?? 0),
            Gate = controller is null ? null : MayTransmit,
        };

        // A receive-only endpoint still reports, under an SSRC of its own.
        while (_rtcpSenderSsrc == 0)
        {
            _rtcpSenderSsrc = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
        }
    }

    /// <summary>Raised for each gathered local ICE candidate (trickle it to the peer via signaling).</summary>
    public event Action<IceCandidate>? LocalIceCandidate;

    /// <summary>Raised when the connection state changes.</summary>
    public event Action<PeerConnectionState>? StateChanged;

    /// <summary>
    /// Raised for each received, decrypted RTP packet (header + payload). The payload is a borrowed slice of
    /// the transport's reused receive buffer, valid only for the synchronous duration of the handler - copy
    /// it if you keep it (a depacketizer that assembles a frame already does). Zero-copy by design.
    /// </summary>
    public event Action<RtpHeader, ReadOnlyMemory<byte>>? RtpReceived;

    /// <summary>Raised when the peer requests a keyframe (PLI/FIR) for the given media SSRC.</summary>
    public event Action<uint>? KeyframeRequested;

    /// <summary>The current connection state.</summary>
    public PeerConnectionState State => (PeerConnectionState)Volatile.Read(ref _state);

    /// <summary>
    /// The negotiated media per section, available once offer/answer completes (after <see cref="CreateAnswer"/>
    /// on the answerer, or <see cref="SetRemoteDescription"/> with an answer on the offerer). The media layer
    /// reads the agreed codec + payload type + send SSRC from here.
    /// </summary>
    public IReadOnlyList<NegotiatedMediaInfo> NegotiatedMedia { get; private set; } = [];

    /// <summary>The local DTLS certificate fingerprint (advertised in offers/answers).</summary>
    public DtlsFingerprint LocalFingerprint => _certificate.Fingerprint;

    /// <summary>Creates the offer, generates local credentials, and starts gathering ICE candidates.</summary>
    public SdpDescription CreateOffer()
    {
        var media = new List<SdpMediaDescription>(_options.Media.Count);
        foreach (MediaLine line in _options.Media)
        {
            media.Add(
                BuildMediaSection(
                    line.Mid,
                    line.Kind,
                    line.Codecs,
                    line.LocalSsrc,
                    SdpSetup.ActPass,
                    line.Direction,
                    line.RtxSsrc
                )
            );
        }

        if (_iceAgent is null)
        {
            StartIce(IceRole.Controlling);
        }

        return new SdpDescription { Media = media };
    }

    /// <summary>Creates the answer to a previously set remote offer, and starts gathering ICE candidates.</summary>
    public SdpDescription CreateAnswer()
    {
        SdpDescription offer =
            _remoteDescription ?? throw new InvalidOperationException("No remote offer set.");

        // We answer as DTLS client (a=setup:active) - the common WebRTC arrangement.
        _dtlsRole = DtlsRole.Client;
        var media = new List<SdpMediaDescription>(offer.Media.Count);
        var negotiated = new List<NegotiatedMediaInfo>(offer.Media.Count);
        foreach (SdpMediaDescription remote in offer.Media)
        {
            uint ssrc = LocalSsrcFor(remote.Mid, remote.Kind);

            // Answer with the offered codecs we also support (matched by encoding name + clock), keeping the
            // offerer's payload types and preference order and describing each with our own format
            // parameters and feedback (RFC 3264). If we support none, echo the offer.
            IReadOnlyList<SdpCodec> local = LocalCodecsFor(remote.Kind);
            List<SdpCodec> offered = [];
            List<SdpCodec> answered = [];
            foreach (SdpCodec rc in remote.Codecs)
            {
                if (Rtx.IsRtx(rc))
                {
                    continue;
                }

                foreach (SdpCodec lc in local)
                {
                    if (Rtx.IsRtx(lc))
                    {
                        continue;
                    }

                    // One payload type per codec: a browser offers several of each (H.264 profiles and
                    // packetization modes), and the first that matches is the one used.
                    if (
                        CodecsMatch(lc, rc)
                        && !answered.Exists(a =>
                            string.Equals(
                                a.EncodingName,
                                lc.EncodingName,
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                    )
                    {
                        offered.Add(rc);
                        answered.Add(lc with { PayloadType = rc.PayloadType });
                        break;
                    }
                }
            }

            // Retransmission for each answered codec the offer pairs with an rtx codec, when this
            // endpoint retransmits on the line too (RFC 4588).
            uint? rtxSsrc = LocalRtxSsrcFor(remote.Mid, remote.Kind);
            if (rtxSsrc is not null)
            {
                foreach (SdpCodec primary in answered.ToArray())
                {
                    if (
                        remote.Codecs.FirstOrDefault(c => Rtx.Repairs(c) == primary.PayloadType) is
                        { EncodingName: not null } repair
                    )
                    {
                        offered.Add(repair);
                        answered.Add(Rtx.For(repair.PayloadType, primary.PayloadType));
                    }
                }
            }

            // A section with no codec in common is rejected (RFC 3264 section 6): port 0, listing the
            // offered formats, and no media negotiated for it.
            if (answered.Count == 0)
            {
                LogSectionRejected(_logger, remote.Kind, remote.Mid);
                media.Add(
                    BuildMediaSection(
                        remote.Mid,
                        remote.Kind,
                        remote.Codecs,
                        ssrc,
                        SdpSetup.Active,
                        SdpDirection.Inactive,
                        rtxSsrc: null
                    ) with
                    {
                        Rejected = true,
                    }
                );
                continue;
            }

            bool repairs = answered.Exists(Rtx.IsRtx);
            IReadOnlyList<SdpCodec> answerCodecs = answered;
            IReadOnlyList<SdpCodec> remoteCodecs = offered;

            media.Add(
                BuildMediaSection(
                    remote.Mid,
                    remote.Kind,
                    answerCodecs,
                    ssrc,
                    SdpSetup.Active,
                    Answer(remote.Direction, LocalDirectionFor(remote.Kind)),
                    repairs ? rtxSsrc : null
                ) with
                {
                    // An answer carries these only when the offer did (RFC 5506, RFC 8888 section 6).
                    RtcpReducedSize = remote.RtcpReducedSize,
                    CongestionControlFeedback = remote.CongestionControlFeedback,
                }
            );
            negotiated.Add(
                new NegotiatedMediaInfo(remote.Kind, remote.Mid, ssrc, answerCodecs, remoteCodecs)
            );
        }

        NegotiatedMedia = negotiated;
        ConfigureRtx(offer);
        if (_iceAgent is null)
        {
            StartIce(IceRole.Controlled);
        }

        return new SdpDescription { Media = media };
    }

    /// <summary>
    /// Waits until ICE has gathered every local candidate (bounded by <paramref name="limit"/>), for a
    /// description that carries them all to a peer that does not trickle.
    /// </summary>
    /// <param name="limit">The longest to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when gathering finished or the limit passed.</returns>
    public Task WhenCandidatesGatheredAsync(
        TimeSpan limit,
        CancellationToken cancellationToken = default
    ) => _iceAgent?.WhenGatheredAsync(limit, cancellationToken) ?? Task.CompletedTask;

    /// <summary>A local description with the candidates gathered so far in every section, ending them.</summary>
    /// <param name="description">The offer or answer.</param>
    /// <returns>The description with its candidates.</returns>
    public SdpDescription WithLocalCandidates(SdpDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        IceCandidate[] candidates;
        lock (_localCandidates)
        {
            candidates = [.. _localCandidates];
        }

        return description with
        {
            Media =
            [
                .. description.Media.Select(m =>
                    m with
                    {
                        Candidates = candidates,
                        EndOfCandidates = true,
                    }
                ),
            ],
        };
    }

    // A restart gathers anew; the old candidates belong to the old credentials.
    private void ForgetLocalCandidates()
    {
        lock (_localCandidates)
        {
            _localCandidates.Clear();
        }
    }

    /// <summary>The path media takes, once ICE has selected one; null before and during a recovery.</summary>
    public IcePath? SelectedPath => _iceAgent?.SelectedPath;

    /// <summary>
    /// Begin an ICE restart (RFC 8829): adopt fresh local ICE credentials, restart the agent (re-gather), and
    /// return a new offer to send. The DTLS-SRTP session is preserved, so media continues. The peer applies
    /// the new credentials and restarts in turn when it sees the changed ufrag in this offer.
    /// </summary>
    public SdpDescription RestartIce()
    {
        _localIceCredentials = IceCredentials.Generate();
        ForgetLocalCandidates();
        _iceAgent?.Restart(_localIceCredentials);

        // Build the offer from current media with the new credentials; the agent already exists (restarted),
        // so unlike CreateOffer this does not start a fresh ICE agent.
        var media = new List<SdpMediaDescription>(_options.Media.Count);
        foreach (MediaLine line in _options.Media)
        {
            media.Add(
                BuildMediaSection(
                    line.Mid,
                    line.Kind,
                    line.Codecs,
                    line.LocalSsrc,
                    SdpSetup.ActPass,
                    line.Direction,
                    line.RtxSsrc
                )
            );
        }

        return new SdpDescription { Media = media };
    }

    /// <summary>Applies the peer's session description and, for an answer, fixes the DTLS role.</summary>
    public void SetRemoteDescription(SdpDescription description, SdpType type)
    {
        ArgumentNullException.ThrowIfNull(description);
        SdpMediaDescription first = description.Media[0];

        // An offer whose ICE ufrag differs from the established one is an ICE restart (RFC 8829): rotate our
        // own credentials and restart the agent so the answer carries fresh creds + re-gathered candidates.
        bool isRestartOffer =
            type == SdpType.Offer
            && _iceAgent is not null
            && _remoteDescription is { Media: [{ IceUfrag: { } prevUfrag }, ..] }
            && prevUfrag != first.IceUfrag;

        _remoteDescription = description;
        NoteRtcpCapabilities(description);
        _expectedRemoteFingerprint = first.Fingerprint;

        if (isRestartOffer)
        {
            _localIceCredentials = IceCredentials.Generate();
            ForgetLocalCandidates();
            _iceAgent!.Restart(_localIceCredentials);
        }

        if (type == SdpType.Answer)
        {
            // Offerer learns the role from the answerer's choice: their active => we are the DTLS server.
            _dtlsRole = first.Setup == SdpSetup.Active ? DtlsRole.Server : DtlsRole.Client;

            // Record what the answerer accepted (in our payload-type space) as the negotiated media.
            var negotiated = new List<NegotiatedMediaInfo>(description.Media.Count);
            foreach (SdpMediaDescription answered in description.Media)
            {
                if (answered.Rejected)
                {
                    LogSectionRejected(_logger, answered.Kind, answered.Mid);
                    continue;
                }

                IReadOnlyList<SdpCodec> offered = LocalCodecsFor(answered.Kind);
                List<SdpCodec> ours =
                [
                    .. answered.Codecs.Select(ac =>
                        offered.FirstOrDefault(oc => oc.PayloadType == ac.PayloadType)
                            is { EncodingName: not null } oc
                            ? oc
                            : ac
                    ),
                ];
                negotiated.Add(
                    new NegotiatedMediaInfo(
                        answered.Kind,
                        answered.Mid,
                        LocalSsrcFor(answered.Mid, answered.Kind),
                        ours,
                        answered.Codecs
                    )
                );
            }

            NegotiatedMedia = negotiated;
            ConfigureRtx(description);
        }

        _iceAgent?.SetRemoteCredentials(new IceCredentials(first.IceUfrag, first.IcePwd));

        // Candidates the description carries, as a peer that does not trickle sends them.
        foreach (
            IceCandidate candidate in description
                .Media.SelectMany(static m => m.Candidates)
                .Distinct()
        )
        {
            AddRemoteIceCandidate(candidate);
        }

        lock (_gate)
        {
            if (_iceAgent is { } agent)
            {
                foreach (IceCandidate candidate in _bufferedRemoteCandidates)
                {
                    agent.AddRemoteCandidate(candidate);
                }

                _bufferedRemoteCandidates.Clear();
            }
        }
    }

    /// <summary>
    /// Trigger ICE mobility recovery: re-probe candidate pairs and fail over to whatever path now works,
    /// preserving the DTLS-SRTP session (keys + ROC). Called on a network change (or internally on consent
    /// loss). No-op before ICE starts.
    /// </summary>
    public void TriggerNetworkRecovery() => _iceAgent?.TriggerRecovery();

    /// <summary>Adds a remote ICE candidate (trickle), buffering it if ICE has not started yet.</summary>
    public void AddRemoteIceCandidate(IceCandidate candidate)
    {
        lock (_gate)
        {
            if (_iceAgent is { } agent)
            {
                agent.AddRemoteCandidate(candidate);
            }
            else
            {
                _bufferedRemoteCandidates.Add(candidate);
            }
        }
    }

    /// <summary>
    /// Queues a media payload as an RTP packet at the connection's pacer, which protects and sends it in
    /// turn with the retransmissions and FEC repairs that share its budget. The sequence number is managed
    /// per SSRC; abs-capture-time is added when configured and <paramref name="captureNtp"/> is set. The
    /// payload is copied once, into the packet.
    /// </summary>
    /// <param name="payloadType">The RTP payload type.</param>
    /// <param name="ssrc">The stream's SSRC.</param>
    /// <param name="rtpTimestamp">The RTP timestamp.</param>
    /// <param name="marker">The marker bit.</param>
    /// <param name="payload">The payload.</param>
    /// <param name="captureNtp">The abs-capture-time, or zero for none.</param>
    /// <returns>False, dropping the packet, when DTLS-SRTP is not established yet.</returns>
    public bool TrySendRtp(
        byte payloadType,
        uint ssrc,
        uint rtpTimestamp,
        bool marker,
        ReadOnlySpan<byte> payload,
        ulong captureNtp = 0
    )
    {
        if (_srtp is null || TransmissionCeased)
        {
            return false;
        }

        ushort sequence = NextSequence(ssrc);

        // RTP header + optional abs-capture-time + payload, with room to protect it in place. Rented from
        // the shared pool so the per-packet path does not allocate (matters on an SBC's GC).
        byte[] buffer = ArrayPool<byte>.Shared.Rent(
            RtpPacket.FixedHeaderLength + 16 + payload.Length + SrtpSession.MaxProtectionOverhead
        );
        int rtpLength = RtpPacket.Write(
            buffer,
            marker,
            payloadType,
            sequence,
            rtpTimestamp,
            ssrc,
            payload,
            captureNtp != 0 ? _options.AbsCaptureTimeExtensionId : 0,
            captureNtp
        );
        ReadOnlySpan<byte> packet = buffer.AsSpan(0, rtpLength);

        // FlexFEC protects the video stream: the cleartext is what FEC XORs.
        byte[]? fecRepair =
            FecEnabled && ssrc == _options.FecProtectedSsrc ? AccumulateFec(packet) : null;

        // Kept for a NACK-driven RTX retransmission.
        if (Volatile.Read(ref _rtx).Send.ContainsKey(ssrc))
        {
            _sendHistory.GetOrAdd(ssrc, static _ => new RtpSendHistory()).Store(sequence, packet);
        }

        _pacer.Enqueue(
            new PacedPacket(
                buffer,
                rtpLength,
                _audioSsrcs.Contains(ssrc) ? TrafficClass.Audio : TrafficClass.Video
            )
        );

        // The repair rides its own SSRC behind the group it protects, and is not itself protected.
        if (fecRepair is not null)
        {
            byte[] repair = ArrayPool<byte>.Shared.Rent(
                RtpPacket.FixedHeaderLength + fecRepair.Length + SrtpSession.MaxProtectionOverhead
            );
            int repairLength = RtpPacket.Write(
                repair,
                false,
                _options.FecPayloadType,
                NextSequence(_options.FecSsrc),
                0,
                _options.FecSsrc,
                fecRepair,
                0,
                0
            );
            _pacer.Enqueue(new PacedPacket(repair, repairLength, TrafficClass.Repair));
        }

        return true;
    }

    /// <summary>
    /// Requests a keyframe from the peer by sending a Picture Loss Indication (RFC 4585) for the given
    /// media SSRC. Called by a receiver whose decoder needs a fresh intra frame (e.g. on join or after loss).
    /// </summary>
    public async ValueTask RequestKeyframeAsync(
        uint mediaSsrc,
        CancellationToken cancellationToken = default
    )
    {
        if (_srtp is not { } srtp || _iceAgent is not { } agent)
        {
            return;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(
            FeedbackPrefixCapacity + 32 + SrtpSession.MaxRtcpProtectionOverhead
        );
        try
        {
            int prefix = WriteFeedbackPrefix(buffer);
            int length =
                prefix + RtcpFeedback.BuildPli(buffer.AsSpan(prefix), _rtcpSenderSsrc, mediaSsrc);
            int protectedLength = srtp.ProtectRtcp(buffer, length);
            Interlocked.Increment(ref _keyframeRequestsSent);
            await agent
                .SendAsync(buffer.AsMemory(0, protectedLength), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private ushort NextSequence(uint ssrc)
    {
        lock (_gate)
        {
            ushort sequence = _sendSequence.TryGetValue(ssrc, out ushort s)
                ? s
                : (ushort)Random.Shared.Next(0, 0x10000);
            _sendSequence[ssrc] = (ushort)(sequence + 1);
            return sequence;
        }
    }

    // The pacer's send: protect in place, account for it (congestion feedback, sender reports), send.
    // The pacer runs one send at a time in queue order, so SRTP protection is never concurrent.
    private ValueTask TransmitAsync(PacedPacket packet, CancellationToken cancellationToken)
    {
        if (
            _srtp is not { } srtp
            || _iceAgent is not { } agent
            || TransmissionCeased
            || !RtpPacket.TryParse(
                packet.Buffer.AsSpan(0, packet.Length),
                out RtpHeader header,
                out ReadOnlySpan<byte> payload
            )
        )
        {
            return ValueTask.CompletedTask;
        }

        int payloadLength = payload.Length;
        int protectedLength = srtp.ProtectRtp(packet.Buffer, packet.Length);
        long now = NowMicros();
        RecordSent(header.Ssrc, header.SequenceNumber, protectedLength, packet.Class);
        BreakerOnSent(header.Ssrc, protectedLength);
        RecordSentForReports(header.Ssrc, header.PayloadType, header.Timestamp, payloadLength, now);
        if (packet.Class == TrafficClass.Retransmission)
        {
            Interlocked.Increment(ref _rtxPacketsSent);
            _metrics.Retransmissions.Add(1, WebRtcMetrics.Direction("sent"));
        }
        else
        {
            Interlocked.Increment(ref _mediaPacketsSent);
            _metrics.PacketsSent.Add(1);
        }

        return agent.SendAsync(packet.Buffer.AsMemory(0, protectedLength), cancellationToken);
    }

    /// <summary>
    /// The bytes the connection has sent in a traffic class, with per-packet overhead; two samples give
    /// the class's rate.
    /// </summary>
    /// <param name="trafficClass">The class.</param>
    /// <returns>The bytes.</returns>
    public long SentBytes(TrafficClass trafficClass) => _pacer.SentBytes(trafficClass);

    private void StartIce(IceRole role)
    {
        var agent = new IceAgent(
            _localIceCredentials,
            role,
            _options.IncludeLoopback,
            _loggerFactory.CreateLogger<IceAgent>(),
            socketFactory: _options.SocketFactory
                ?? new UdpIceSocketFactory(_options.LocalAddressPreferences),
            timeProvider: _time,
            transportPolicy: _options.IceTransportPolicy,
            meterFactory: _meterFactory
        );
        foreach (IPEndPoint stun in _options.StunServers)
        {
            agent.AddStunServer(stun);
        }

        foreach (TurnServer turn in _options.TurnServers)
        {
            agent.AddTurnServer(turn);
        }

        agent.LocalCandidateGathered += c =>
        {
            lock (_localCandidates)
            {
                _localCandidates.Add(c);
            }

            LocalIceCandidate?.Invoke(c);
        };
        _dtlsTransport = new IceDatagramTransport(agent);
        agent.DataReceived += OnTransportData;
        agent.StateChanged += OnIceStateChanged;

        if (_remoteDescription is { } remote)
        {
            SdpMediaDescription first = remote.Media[0];
            agent.SetRemoteCredentials(new IceCredentials(first.IceUfrag, first.IcePwd));
        }

        lock (_gate)
        {
            _iceAgent = agent;
            agent.Start();
            foreach (IceCandidate candidate in _bufferedRemoteCandidates)
            {
                agent.AddRemoteCandidate(candidate);
            }

            _bufferedRemoteCandidates.Clear();
        }

        // From starting ICE to the secure transport being up is one span and one measurement.
        _connectStarted = _time.GetTimestamp();
        _connecting = WebRtcDiagnostics.ActivitySource.StartActivity(
            "streamtransport.webrtc.connect"
        );
        _connecting?.SetTag(
            "streamtransport.webrtc.ice.role",
            role == IceRole.Controlling ? "controlling" : "controlled"
        );
        SetState(PeerConnectionState.Connecting);
    }

    // Records how connecting ended and closes its span, once.
    private void Settle(string outcome, Exception? failure = null)
    {
        if (Interlocked.Exchange(ref _connectSettled, 1) != 0 || _connectStarted == 0)
        {
            return;
        }

        IcePath? path = _iceAgent?.SelectedPath;
        _metrics.ConnectDuration.Record(
            _time.GetElapsedTime(_connectStarted).TotalSeconds,
            WebRtcMetrics.Outcome(outcome)
        );
        if (_connecting is { } activity)
        {
            activity.SetTag("streamtransport.outcome", outcome);
            if (path is { } selected)
            {
                activity.SetTag(
                    "streamtransport.webrtc.ice.local_kind",
                    WebRtcMetrics.Name(selected.LocalKind)
                );
                activity.SetTag(
                    "streamtransport.webrtc.ice.remote_kind",
                    WebRtcMetrics.Name(selected.RemoteKind)
                );
                activity.SetTag(
                    "network.type",
                    selected.Remote.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                        ? "ipv6"
                        : "ipv4"
                );
            }

            if (failure is not null)
            {
                activity.AddException(failure);
                activity.SetStatus(ActivityStatusCode.Error, failure.Message);
            }

            activity.Stop();
        }
    }

    private void OnIceStateChanged(IceConnectionState iceState)
    {
        switch (iceState)
        {
            case IceConnectionState.Connected when Interlocked.Exchange(ref _dtlsStarted, 1) == 0:
                _ = RunDtlsHandshakeAsync();
                break;
            case IceConnectionState.Failed:
                Settle("ice_failed");
                SetState(PeerConnectionState.Failed);
                break;
            default:
                break;
        }
    }

    private async Task RunDtlsHandshakeAsync()
    {
        // The remote certificate is authenticated during the handshake by the fingerprint the peer
        // signalled; a description without one never reaches here, SdpReader rejects it.
        RemoteCertificateValidationCallback validate = _expectedRemoteFingerprint is { } expected
            ? expected.CreateValidationCallback()
            : static (_, _, _, _) => false;
        IceDatagramTransport transport = _dtlsTransport!;

        try
        {
            DtlsConnection dtls =
                _dtlsRole == DtlsRole.Client
                    ? await DtlsConnection
                        .ConnectAsync(transport, ClientOptions(validate))
                        .ConfigureAwait(false)
                    : await DtlsConnection
                        .AcceptAsync(transport, ServerOptions(validate))
                        .ConfigureAwait(false);
            _dtls = dtls;

            SrtpKeyingMaterial keying =
                dtls.SrtpKeyingMaterial
                ?? throw new DtlsException("The peer negotiated no SRTP protection profile.");
            _srtp = new SrtpSession(keying, _dtlsRole == DtlsRole.Client);
            LogConnected(_logger, dtls.NegotiatedProtocol, keying.Profile);
            Settle("connected");
            SetState(PeerConnectionState.Connected);
        }
        catch (Exception ex)
        {
            LogHandshakeFailed(_logger, ex);
            Settle("dtls_failed", ex);
            SetState(PeerConnectionState.Failed);
        }
    }

    private DtlsClientConnectionOptions ClientOptions(
        RemoteCertificateValidationCallback validate
    ) =>
        new()
        {
            SrtpProtectionProfiles = [.. SrtpSession.Profiles],
            LoggerFactory = _loggerFactory,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = [_certificate.Certificate],
                RemoteCertificateValidationCallback = validate,
            },
        };

    // No cookie exchange: ICE connectivity checks have already proven the peer owns its address.
    private DtlsServerConnectionOptions ServerOptions(
        RemoteCertificateValidationCallback validate
    ) =>
        new()
        {
            SrtpProtectionProfiles = [.. SrtpSession.Profiles],
            LoggerFactory = _loggerFactory,
            CookieExchange = false,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate.Certificate,
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = validate,
            },
        };

    private void OnTransportData(Memory<byte> data, IPEndPoint source, byte ecn)
    {
        Span<byte> span = data.Span;
        if (span.IsEmpty)
        {
            return;
        }

        byte first = span[0];
        if (first is >= 20 and <= 63)
        {
            _dtlsTransport?.Deliver(span);
            return;
        }

        if (first is >= 128 and <= 191)
        {
            int pt = span[1] & 0x7F;
            if (pt is >= 64 and <= 95)
            {
                ReceiveRtcp(data);
            }
            else
            {
                ReceiveRtp(data, ecn);
            }
        }
    }

    private void ReceiveRtcp(Memory<byte> data)
    {
        if (_srtp is not { } srtp)
        {
            return;
        }

        // Decrypt in place into the owned buffer - no copy.
        Span<byte> span = data.Span;
        if (!srtp.UnprotectRtcp(span, span.Length, out int length))
        {
            return;
        }

        ReadOnlySpan<byte> rtcp = span[..length];
        BreakerOnFeedback();

        if (RtcpFeedback.ContainsPli(rtcp, out uint pliSsrc))
        {
            KeyframeRequested?.Invoke(pliSsrc);
        }

        var lost = new List<ushort>();
        if (RtcpFeedback.TryParseNack(rtcp, out uint nackSsrc, lost) && lost.Count > 0)
        {
            Retransmit(nackSsrc, lost);
        }

        // RFC 8888 congestion-control feedback (PT 205, FMT 11) - drives the send-side controller, if any.
        OnCongestionFeedback(rtcp);

        // Sender and receiver reports: the round trip and the peer's sender-report timing.
        OnReports(rtcp);
    }

    // Repairs go through the pacer ahead of new video and inside the same budget (RFC 4588 section 7).
    private void Retransmit(uint mediaSsrc, List<ushort> lostSequences)
    {
        if (
            !Volatile.Read(ref _rtx).Send.TryGetValue(mediaSsrc, out RtxSender? rtx)
            || !_sendHistory.TryGetValue(mediaSsrc, out RtpSendHistory? history)
        )
        {
            return;
        }

        foreach (ushort seq in lostSequences)
        {
            // The repair travels with the rtx payload type paired with the original's (RFC 4588).
            if (
                !history.TryGet(seq, out ReadOnlyMemory<byte> original)
                || original.Length < 2
                || !rtx.PayloadTypes.TryGetValue(
                    (byte)(original.Span[1] & 0x7F),
                    out byte rtxPayloadType
                )
            )
            {
                continue;
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(
                original.Length + 2 + SrtpSession.MaxProtectionOverhead
            );
            if (
                RtxStream.TryWrap(
                    original.Span,
                    buffer,
                    rtxPayloadType,
                    rtx.Ssrc,
                    rtx.NextSequence(),
                    out int rtxLength
                )
            )
            {
                _pacer.Enqueue(new PacedPacket(buffer, rtxLength, TrafficClass.Retransmission));
            }
            else
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private void ReceiveRtp(Memory<byte> data, byte ecn)
    {
        if (_srtp is not { } srtp)
        {
            return;
        }

        // Decrypt in place into the borrowed buffer - no copy.
        Span<byte> span = data.Span;
        if (
            !srtp.UnprotectRtp(span, span.Length, out int plaintextLength)
            || !RtpPacket.TryParse(
                span[..plaintextLength],
                out RtpHeader header,
                out ReadOnlySpan<byte> payload
            )
        )
        {
            return;
        }

        // Congestion feedback and reception reports cover what crossed the wire, so a retransmission
        // counts under the RTX stream's SSRC and sequence.
        long arrivalMicros = NowMicros();
        RecordArrival(header.Ssrc, header.SequenceNumber, arrivalMicros, ecn, header.Marker);
        RecordReceivedForReports(header, arrivalMicros);

        // RTX retransmission (RFC 4588): unwrap to the original packet and deliver that, so a NACK-recovered
        // packet reaches the depacketizer (and the loss tracker) as if it had never been lost. Done into a
        // rented buffer because the recovered packet is larger than the borrowed receive slice can describe.
        if (
            TryRecognizeRtx(
                header.Ssrc,
                header.PayloadType,
                out uint mediaSsrc,
                out byte originalPayloadType
            )
        )
        {
            byte[] recovered = ArrayPool<byte>.Shared.Rent(plaintextLength);
            try
            {
                if (
                    RtxStream.TryUnwrap(
                        span[..plaintextLength],
                        recovered,
                        originalPayloadType,
                        mediaSsrc,
                        out int recoveredLength
                    )
                    && RtpPacket.TryParse(
                        recovered.AsSpan(0, recoveredLength),
                        out RtpHeader rtxHeader,
                        out ReadOnlySpan<byte> rtxPayload
                    )
                )
                {
                    Interlocked.Increment(ref _rtxPacketsRecovered);
                    _metrics.Retransmissions.Add(1, WebRtcMetrics.Direction("recovered"));
                    DeliverMedia(
                        rtxHeader,
                        recovered.AsMemory(0, recoveredLength),
                        recoveredLength,
                        rtxPayload
                    );
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(recovered);
            }

            return;
        }

        DeliverMedia(header, data, plaintextLength, payload);
    }

    // Common delivery for an original or RTX-recovered media packet: record its arrival for congestion feedback
    // and loss recovery, run FlexFEC, then hand the payload up as a zero-copy slice of its backing buffer.
    private void DeliverMedia(
        RtpHeader header,
        Memory<byte> packet,
        int plaintextLength,
        ReadOnlySpan<byte> payload
    )
    {
        // Loss recovery (NACK): only for media the peer said it retransmits.
        if (Volatile.Read(ref _rtx).Repairable.Contains(header.Ssrc))
        {
            OnMediaSequence(header.Ssrc, header.SequenceNumber);
        }

        // FlexFEC: a repair packet recovers a lost media packet; a protected media packet is cached for recovery.
        if (FecEnabled)
        {
            if (header.PayloadType == _options.FecPayloadType)
            {
                OnFecPacket(payload);
                return;
            }

            if (header.Ssrc == _options.FecProtectedSsrc)
            {
                CacheProtectedPacket(packet.Span[..plaintextLength]);
            }
        }

        int payloadOffset = plaintextLength - payload.Length;
        RtpReceived?.Invoke(header, packet.Slice(payloadOffset, payload.Length));
    }

    private SdpMediaDescription BuildMediaSection(
        string mid,
        SdpMediaKind kind,
        IReadOnlyList<SdpCodec> codecs,
        uint ssrc,
        SdpSetup setup,
        SdpDirection direction,
        uint? rtxSsrc
    ) =>
        new()
        {
            Kind = kind,
            Mid = mid,
            Direction = direction,
            Codecs = codecs,
            IceUfrag = _localIceCredentials.UsernameFragment,
            IcePwd = _localIceCredentials.Password,
            Fingerprint = _certificate.Fingerprint,
            Setup = setup,
            Ssrc = ssrc == 0 ? null : ssrc,
            RtxSsrc = ssrc == 0 ? null : rtxSsrc,
            Cname = _cname,
        };

    // The line of a section: the same mid and kind, else the same kind. A peer's offer numbers its
    // sections its own way (a browser may put video first), so the kind decides.
    private uint LocalSsrcFor(string mid, SdpMediaKind kind) =>
        (
            _options.Media.FirstOrDefault(l => l.Kind == kind && l.Mid == mid)
            ?? _options.Media.FirstOrDefault(l => l.Kind == kind)
        )?.LocalSsrc
        ?? 0;

    private uint? LocalRtxSsrcFor(string mid, SdpMediaKind kind) =>
        (
            _options.Media.FirstOrDefault(l => l.Kind == kind && l.Mid == mid)
            ?? _options.Media.FirstOrDefault(l => l.Kind == kind)
        )?.RtxSsrc;

    // What retransmission the negotiation agreed: per line, an rtx codec on both sides for a media
    // codec. This endpoint sends repairs of its media on its RTX SSRC with the rtx payload type paired
    // with the original's; it asks for repairs (NACK) of the peer's media the peer announced an RTX SSRC
    // for, and reads them back by that SSRC.
    private void ConfigureRtx(SdpDescription remote)
    {
        Dictionary<uint, RtxSender> send = [];
        Dictionary<uint, RtxReceiver> receive = [];
        HashSet<uint> repairable = [];
        foreach (NegotiatedMediaInfo media in NegotiatedMedia)
        {
            Dictionary<byte, byte> rtxFor = [];
            foreach (SdpCodec codec in media.Codecs)
            {
                if (Rtx.Repairs(codec) is { } primary)
                {
                    rtxFor[(byte)primary] = (byte)codec.PayloadType;
                }
            }

            if (rtxFor.Count == 0)
            {
                continue;
            }

            if (LocalRtxSsrcFor(media.Mid, media.Kind) is { } localRtx && media.LocalSsrc != 0)
            {
                send[media.LocalSsrc] = new RtxSender(localRtx, rtxFor.ToFrozenDictionary());
            }

            if (
                remote.Media.FirstOrDefault(m => m.Mid == media.Mid) is
                { Ssrc: { } remoteMedia, RtxSsrc: { } remoteRtx }
            )
            {
                receive[remoteRtx] = new RtxReceiver(
                    remoteMedia,
                    rtxFor.ToFrozenDictionary(static p => p.Value, static p => p.Key)
                );
                _ = repairable.Add(remoteMedia);
            }
        }

        Volatile.Write(
            ref _rtx,
            new RtxConfiguration(
                send.ToFrozenDictionary(),
                receive.ToFrozenDictionary(),
                repairable.ToFrozenSet()
            )
        );
        LogRtx(send.Count, receive.Count);
    }

    // The codecs this endpoint can handle for a kind, taken from the configured offer lines.
    private IReadOnlyList<SdpCodec> LocalCodecsFor(SdpMediaKind kind)
    {
        foreach (MediaLine line in _options.Media)
        {
            if (line.Kind == kind)
            {
                return line.Codecs;
            }
        }

        return [];
    }

    // Two codecs are the same format when the encoding name (case-insensitive) and clock rate agree. Payload
    // types may differ between offer and local config; the answer keeps the offerer's.
    // The same codec: name and clock, and for H.264 the same packetization mode (RFC 6184 section 8.1).
    // The same codec: name and clock rate, and the parameters its payload format says must agree.
    private bool CodecsMatch(SdpCodec local, SdpCodec remote) =>
        string.Equals(local.EncodingName, remote.EncodingName, StringComparison.OrdinalIgnoreCase)
        && local.ClockRate == remote.ClockRate
        && (
            !_options.PayloadFormats.TryGet(local.EncodingName, out RtpPayloadFormat? format)
            || format.AreCompatible(local.FormatParameters, remote.FormatParameters)
        );

    // What this endpoint does on a kind of media, from its lines; send and receive when it has none.
    private SdpDirection LocalDirectionFor(SdpMediaKind kind) =>
        _options.Media.FirstOrDefault(l => l.Kind == kind)?.Direction ?? SdpDirection.SendRecv;

    // The answer's direction: the offer's mirrored, then only what this endpoint does (RFC 3264 section 6.1).
    private static SdpDirection Answer(SdpDirection offered, SdpDirection local)
    {
        bool send = offered is SdpDirection.SendRecv or SdpDirection.RecvOnly;
        bool receive = offered is SdpDirection.SendRecv or SdpDirection.SendOnly;
        send &= local is SdpDirection.SendRecv or SdpDirection.SendOnly;
        receive &= local is SdpDirection.SendRecv or SdpDirection.RecvOnly;
        return (send, receive) switch
        {
            (true, true) => SdpDirection.SendRecv,
            (true, false) => SdpDirection.SendOnly,
            (false, true) => SdpDirection.RecvOnly,
            _ => SdpDirection.Inactive,
        };
    }

    private void SetState(PeerConnectionState state)
    {
        if (Volatile.Read(ref _state) == (int)state)
        {
            return;
        }

        Volatile.Write(ref _state, (int)state);
        if (state == PeerConnectionState.Connected)
        {
            StartCongestionTimers();
        }

        StateChanged?.Invoke(state);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposedForMetrics, 1) == 0)
        {
            _metrics.ConnectionsActive.Add(-1);
            Settle("closed");
        }

        DisposeCongestion();
        await _pacer.DisposeAsync().ConfigureAwait(false);
        await StopReportsAsync().ConfigureAwait(false);
        SetState(PeerConnectionState.Closed);
        if (_dtls is { } dtls)
        {
            await dtls.DisposeAsync().ConfigureAwait(false);
        }

        _dtlsTransport?.Complete();

        if (_iceAgent is { } agent)
        {
            await agent.DisposeAsync().ConfigureAwait(false);
        }

        // After the agent: nothing arrives to unprotect any more, and a send racing the close fails.
        Interlocked.Exchange(ref _srtp, null)?.Dispose();
    }

    // Repairs of one of this endpoint's media streams: the RTX SSRC, the rtx payload type for each
    // media payload type, and the RTX stream's own sequence.
    private sealed class RtxSender(uint ssrc, FrozenDictionary<byte, byte> payloadTypes)
    {
        private int _sequence;

        public uint Ssrc { get; } = ssrc;

        public FrozenDictionary<byte, byte> PayloadTypes { get; } = payloadTypes;

        public ushort NextSequence() => (ushort)Interlocked.Increment(ref _sequence);
    }

    // The peer's repairs of one of its media streams: the media SSRC, and the original payload type for
    // each rtx payload type.
    private sealed record RtxReceiver(
        uint MediaSsrc,
        FrozenDictionary<byte, byte> OriginalPayloadTypes
    );

    private sealed record RtxConfiguration(
        FrozenDictionary<uint, RtxSender> Send,
        FrozenDictionary<uint, RtxReceiver> Receive,
        FrozenSet<uint> Repairable
    )
    {
        public static RtxConfiguration None { get; } = new([], [], []);
    }

    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Information,
        Message = "PeerConnection established ({Protocol}, SRTP {Profile})"
    )]
    private static partial void LogConnected(
        ILogger logger,
        DtlsProtocols protocol,
        SrtpProtectionProfile profile
    );

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Error,
        Message = "PeerConnection DTLS handshake failed"
    )]
    private static partial void LogHandshakeFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Debug,
        Message = "Retransmission negotiated: this side repairs {Sent} streams and asks for repairs of {Received}."
    )]
    private partial void LogRtx(int sent, int received);

    [LoggerMessage(
        EventId = 1103,
        Level = LogLevel.Warning,
        Message = "The {Kind} section {Mid} is rejected: the peers share no codec for it."
    )]
    private static partial void LogSectionRejected(ILogger logger, SdpMediaKind kind, string mid);
}
