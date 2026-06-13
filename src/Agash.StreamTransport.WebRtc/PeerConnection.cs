using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using Agash.StreamTransport.WebRtc.Ice;
using Agash.StreamTransport.WebRtc.Rtcp;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Sdp;
using Agash.StreamTransport.WebRtc.Srtp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// A point-to-point WebRTC peer connection: it composes the ICE agent, the DTLS-SRTP transport, and the
/// SRTP/RTP layers behind a JSEP-style offer/answer surface. One BUNDLE transport carries all media
/// (rtcp-mux). Protected media is sent with <see cref="SendRtp"/> and surfaced via <see cref="RtpReceived"/>.
/// </summary>
public sealed partial class PeerConnection : IAsyncDisposable
{
    private readonly PeerConnectionOptions _options;
    private readonly IDtlsTransportFactory _dtlsFactory;
    private readonly ILogger _logger;
    private IceCredentials _localIceCredentials = IceCredentials.Generate();
    private readonly List<IceCandidate> _bufferedRemoteCandidates = [];
    private readonly Dictionary<uint, ushort> _sendSequence = [];
    private readonly ConcurrentDictionary<uint, RtpSendHistory> _sendHistory = new();
    private readonly Dictionary<uint, RtxState> _rtx = [];
    private readonly Lock _gate = new();
    private uint _rtcpSenderSsrc;

    private IceAgent? _iceAgent;
    private IDtlsTransport? _dtls;
    private SrtpSession? _srtp;
    private SdpDescription? _remoteDescription;
    private DtlsRole _dtlsRole = DtlsRole.Client;
    private DtlsFingerprint? _expectedRemoteFingerprint;
    private int _state = (int)PeerConnectionState.New;

    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Creates a peer connection. The DTLS factory supplies the certificate and handshake engine.</summary>
    /// <param name="options">The media and ICE configuration.</param>
    /// <param name="dtlsFactory">The DTLS-SRTP engine.</param>
    /// <param name="loggerFactory">Optional logging.</param>
    /// <param name="controller">
    /// Optional send-side congestion controller. When supplied, inbound RFC 8888 feedback drives it and the
    /// resulting <see cref="BitrateEstimateChanged"/> estimates retune the encoder/pacer; when null, only
    /// receive-side feedback generation runs.
    /// </param>
    public PeerConnection(
        PeerConnectionOptions options,
        IDtlsTransportFactory dtlsFactory,
        ILoggerFactory? loggerFactory = null,
        INetworkController? controller = null)
    {
        _options = options;
        _dtlsFactory = dtlsFactory;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<PeerConnection>();
        _controller = controller;

        foreach (MediaLine line in options.Media)
        {
            if (_rtcpSenderSsrc == 0)
            {
                _rtcpSenderSsrc = line.LocalSsrc;
            }

            if (line is { RtxSsrc: { } rtxSsrc, RtxPayloadType: { } rtxPt })
            {
                _rtx[line.LocalSsrc] = new RtxState(rtxSsrc, rtxPt);
            }
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
    public DtlsFingerprint LocalFingerprint => _dtlsFactory.LocalFingerprint;

    /// <summary>Creates the offer, generates local credentials, and starts gathering ICE candidates.</summary>
    public SdpDescription CreateOffer()
    {
        var media = new List<SdpMediaDescription>(_options.Media.Count);
        foreach (MediaLine line in _options.Media)
        {
            media.Add(BuildMediaSection(line.Mid, line.Kind, line.Codecs, line.LocalSsrc, SdpSetup.ActPass));
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
        SdpDescription offer = _remoteDescription ?? throw new InvalidOperationException("No remote offer set.");

        // We answer as DTLS client (a=setup:active) - the common WebRTC arrangement.
        _dtlsRole = DtlsRole.Client;
        var media = new List<SdpMediaDescription>(offer.Media.Count);
        var negotiated = new List<NegotiatedMediaInfo>(offer.Media.Count);
        foreach (SdpMediaDescription remote in offer.Media)
        {
            uint ssrc = LocalSsrcFor(remote.Mid, remote.Kind);

            // Answer with the offered codecs we also support (matched by encoding name + clock), keeping the
            // offerer's payload types and preference order. If we support none, echo the offer (legacy peers).
            IReadOnlyList<SdpCodec> local = LocalCodecsFor(remote.Kind);
            List<SdpCodec> agreed = [.. remote.Codecs.Where(rc => local.Any(lc => CodecsMatch(lc, rc)))];
            IReadOnlyList<SdpCodec> answerCodecs = agreed.Count > 0 ? agreed : remote.Codecs;

            media.Add(BuildMediaSection(remote.Mid, remote.Kind, answerCodecs, ssrc, SdpSetup.Active));
            negotiated.Add(new NegotiatedMediaInfo(remote.Kind, remote.Mid, ssrc, answerCodecs));
        }

        NegotiatedMedia = negotiated;
        if (_iceAgent is null)
        {
            StartIce(IceRole.Controlled);
        }

        return new SdpDescription { Media = media };
    }

    /// <summary>
    /// Begin an ICE restart (RFC 8829): adopt fresh local ICE credentials, restart the agent (re-gather), and
    /// return a new offer to send. The DTLS-SRTP session is preserved, so media continues. The peer applies
    /// the new credentials and restarts in turn when it sees the changed ufrag in this offer.
    /// </summary>
    public SdpDescription RestartIce()
    {
        _localIceCredentials = IceCredentials.Generate();
        _iceAgent?.Restart(_localIceCredentials);

        // Build the offer from current media with the new credentials; the agent already exists (restarted),
        // so unlike CreateOffer this does not start a fresh ICE agent.
        var media = new List<SdpMediaDescription>(_options.Media.Count);
        foreach (MediaLine line in _options.Media)
        {
            media.Add(BuildMediaSection(line.Mid, line.Kind, line.Codecs, line.LocalSsrc, SdpSetup.ActPass));
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
        bool isRestartOffer = type == SdpType.Offer
            && _iceAgent is not null
            && _remoteDescription is { Media: [{ IceUfrag: { } prevUfrag }, ..] }
            && prevUfrag != first.IceUfrag;

        _remoteDescription = description;
        _expectedRemoteFingerprint = first.Fingerprint;

        if (isRestartOffer)
        {
            _localIceCredentials = IceCredentials.Generate();
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
                negotiated.Add(new NegotiatedMediaInfo(
                    answered.Kind, answered.Mid, LocalSsrcFor(answered.Mid, answered.Kind), answered.Codecs));
            }

            NegotiatedMedia = negotiated;
        }

        _iceAgent?.SetRemoteCredentials(new IceCredentials(first.IceUfrag, first.IcePwd));

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
    /// Sends a media payload as a protected RTP packet on the connection's transport. The sequence number
    /// is managed per SSRC; abs-capture-time is added when configured and <paramref name="captureNtp"/> is set.
    /// </summary>
    public async ValueTask SendRtp(byte payloadType, uint ssrc, uint rtpTimestamp, bool marker, ReadOnlyMemory<byte> payload, ulong captureNtp = 0, CancellationToken cancellationToken = default)
    {
        SrtpSession srtp = _srtp ?? throw new InvalidOperationException("DTLS-SRTP is not established.");
        IceAgent agent = _iceAgent ?? throw new InvalidOperationException("ICE is not started.");
        byte[]? fecRepair = null;

        ushort sequence;
        lock (_gate)
        {
            sequence = _sendSequence.TryGetValue(ssrc, out ushort s) ? s : (ushort)Random.Shared.Next(0, 0x10000);
            _sendSequence[ssrc] = (ushort)(sequence + 1);
        }

        // RTP header + optional abs-capture-time + payload, with room for the SRTP tag. Rent from the
        // shared pool so the per-packet send path does not allocate (matters on an SBC's GC).
        int capacity = RtpPacket.FixedHeaderLength + 16 + payload.Length + SrtpSession.ProtectionOverhead;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            int rtpLength = RtpPacket.Write(buffer, marker, payloadType, sequence, rtpTimestamp, ssrc, payload.Span,
                captureNtp != 0 ? _options.AbsCaptureTimeExtensionId : 0, captureNtp);

            // FlexFEC: protect the video stream (the cleartext is what FEC XORs; the FEC packet rides FecSsrc).
            if (FecEnabled && ssrc == _options.FecProtectedSsrc)
            {
                fecRepair = AccumulateFec(buffer.AsSpan(0, rtpLength));
            }

            // Keep the cleartext packet for possible NACK-driven RTX retransmission.
            if (_rtx.ContainsKey(ssrc))
            {
                _sendHistory.GetOrAdd(ssrc, static _ => new RtpSendHistory()).Store(sequence, buffer.AsSpan(0, rtpLength));
            }

            int protectedLength = srtp.ProtectRtp(buffer, rtpLength);
            RecordSent(ssrc, sequence, protectedLength, NowMicros());
            await agent.SendAsync(buffer.AsMemory(0, protectedLength), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Send the FlexFEC repair (if a group just completed) after the media packet, on the same chain - so
        // SRTP protect is never invoked concurrently. The FEC packet rides its own SSRC and is not FEC-protected.
        if (fecRepair is not null)
        {
            await SendRtp(_options.FecPayloadType, _options.FecSsrc, 0, marker: false, fecRepair, 0, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Requests a keyframe from the peer by sending a Picture Loss Indication (RFC 4585) for the given
    /// media SSRC. Called by a receiver whose decoder needs a fresh intra frame (e.g. on join or after loss).
    /// </summary>
    public async ValueTask RequestKeyframeAsync(uint mediaSsrc, CancellationToken cancellationToken = default)
    {
        if (_srtp is not { } srtp || _iceAgent is not { } agent)
        {
            return;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(32 + SrtpSession.RtcpProtectionOverhead);
        try
        {
            int length = RtcpFeedback.BuildPli(buffer, _rtcpSenderSsrc, mediaSsrc);
            int protectedLength = srtp.ProtectRtcp(buffer, length);
            await agent.SendAsync(buffer.AsMemory(0, protectedLength), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void StartIce(IceRole role)
    {
        var agent = new IceAgent(
            _localIceCredentials,
            role,
            _options.IncludeLoopback,
            _loggerFactory.CreateLogger<IceAgent>(),
            socketFactory: new UdpIceSocketFactory(_options.LocalAddressPreferences));
        foreach (IPEndPoint stun in _options.StunServers)
        {
            agent.AddStunServer(stun);
        }

        agent.LocalCandidateGathered += c => LocalIceCandidate?.Invoke(c);
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

        SetState(PeerConnectionState.Connecting);
    }

    private void OnIceStateChanged(IceConnectionState iceState)
    {
        switch (iceState)
        {
            case IceConnectionState.Connected when _dtls is null:
                _ = RunDtlsHandshakeAsync();
                break;
            case IceConnectionState.Failed:
                SetState(PeerConnectionState.Failed);
                break;
            default:
                break;
        }
    }

    private async Task RunDtlsHandshakeAsync()
    {
        IceAgent agent = _iceAgent!;
        IDtlsTransport dtls = _dtlsFactory.Create(_dtlsRole, record => _ = agent.SendAsync(record));
        _dtls = dtls;

        try
        {
            SrtpKeyingMaterial keying = await dtls.HandshakeAsync(CancellationToken.None).ConfigureAwait(false);

            if (_expectedRemoteFingerprint is { } expected && dtls.RemoteFingerprint is { } actual
                && !expected.ToSdpValue().Equals(actual.ToSdpValue(), StringComparison.OrdinalIgnoreCase))
            {
                LogFingerprintMismatch(_logger);
                SetState(PeerConnectionState.Failed);
                return;
            }

            _srtp = new SrtpSession(keying, _dtlsRole == DtlsRole.Client);
            LogConnected(_logger, keying.Profile);
            SetState(PeerConnectionState.Connected);
        }
        catch (Exception ex)
        {
            LogHandshakeFailed(_logger, ex);
            SetState(PeerConnectionState.Failed);
        }
    }

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
            _dtls?.ReceiveRecord(data); // DTLS record (Memory implicitly converts to ReadOnlyMemory)
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

        if (RtcpFeedback.ContainsPli(rtcp, out uint pliSsrc))
        {
            KeyframeRequested?.Invoke(pliSsrc);
        }

        var lost = new List<ushort>();
        if (RtcpFeedback.TryParseNack(rtcp, out uint nackSsrc, lost) && lost.Count > 0)
        {
            _ = RetransmitAsync(nackSsrc, lost);
        }

        // RFC 8888 congestion-control feedback (PT 205, FMT 11) - drives the send-side controller, if any.
        OnCongestionFeedback(rtcp);
    }

    private async Task RetransmitAsync(uint mediaSsrc, List<ushort> lostSequences)
    {
        if (_srtp is not { } srtp || _iceAgent is not { } agent
            || !_rtx.TryGetValue(mediaSsrc, out RtxState? rtx)
            || !_sendHistory.TryGetValue(mediaSsrc, out RtpSendHistory? history))
        {
            return;
        }

        foreach (ushort seq in lostSequences)
        {
            if (!history.TryGet(seq, out ReadOnlyMemory<byte> original))
            {
                continue;
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(original.Length + 2 + SrtpSession.ProtectionOverhead);
            try
            {
                if (!RtxStream.TryWrap(original.Span, buffer, rtx.PayloadType, rtx.Ssrc, rtx.NextSequence(), out int rtxLength))
                {
                    continue;
                }

                int protectedLength = srtp.ProtectRtp(buffer, rtxLength);
                await agent.SendAsync(buffer.AsMemory(0, protectedLength)).ConfigureAwait(false);
            }
            finally
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
        if (!srtp.UnprotectRtp(span, span.Length, out int plaintextLength)
            || !RtpPacket.TryParse(span[..plaintextLength], out RtpHeader header, out ReadOnlySpan<byte> payload))
        {
            return;
        }

        // Hand the payload up as a slice of the same buffer (the payload is the tail of the packet). Zero
        // copy - the subscriber copies only if it retains it past the synchronous callback.
        RecordArrival(header.Ssrc, header.SequenceNumber, NowMicros(), ecn);

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
                CacheProtectedPacket(span[..plaintextLength]);
            }
        }

        int payloadOffset = plaintextLength - payload.Length;
        RtpReceived?.Invoke(header, data.Slice(payloadOffset, payload.Length));
    }

    private SdpMediaDescription BuildMediaSection(string mid, SdpMediaKind kind, IReadOnlyList<SdpCodec> codecs, uint ssrc, SdpSetup setup) => new()
    {
        Kind = kind,
        Mid = mid,
        Codecs = codecs,
        IceUfrag = _localIceCredentials.UsernameFragment,
        IcePwd = _localIceCredentials.Password,
        Fingerprint = _dtlsFactory.LocalFingerprint,
        Setup = setup,
        Ssrc = ssrc == 0 ? null : ssrc,
        Cname = "streamtransport",
    };

    private uint LocalSsrcFor(string mid, SdpMediaKind kind)
    {
        foreach (MediaLine line in _options.Media)
        {
            if (line.Mid == mid || line.Kind == kind)
            {
                return line.LocalSsrc;
            }
        }

        return 0;
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
    private static bool CodecsMatch(SdpCodec a, SdpCodec b) =>
        string.Equals(a.EncodingName, b.EncodingName, StringComparison.OrdinalIgnoreCase) && a.ClockRate == b.ClockRate;

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
        DisposeCongestion();
        SetState(PeerConnectionState.Closed);
        if (_dtls is { } dtls)
        {
            await dtls.DisposeAsync().ConfigureAwait(false);
        }

        if (_iceAgent is { } agent)
        {
            await agent.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class RtxState(uint ssrc, byte payloadType)
    {
        private int _sequence;

        public uint Ssrc { get; } = ssrc;
        public byte PayloadType { get; } = payloadType;

        public ushort NextSequence() => (ushort)Interlocked.Increment(ref _sequence);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "PeerConnection established (SRTP {Profile})")]
    private static partial void LogConnected(ILogger logger, SrtpProtectionProfile profile);

    [LoggerMessage(Level = LogLevel.Error, Message = "PeerConnection DTLS fingerprint mismatch - aborting")]
    private static partial void LogFingerprintMismatch(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "PeerConnection DTLS handshake failed")]
    private static partial void LogHandshakeFailed(ILogger logger, Exception exception);
}
