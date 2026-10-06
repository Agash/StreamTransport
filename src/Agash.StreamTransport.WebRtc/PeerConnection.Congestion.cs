using System.Buffers;
using Agash.StreamTransport.WebRtc.Rtcp;
using Agash.StreamTransport.WebRtc.Srtp;

namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// Congestion-control wiring for <see cref="PeerConnection"/>: the receive side records RTP arrivals and
/// periodically sends RFC 8888 Congestion Control Feedback; the send side records sent packets, correlates
/// inbound CCFB into <see cref="PacketResult"/>s for the <see cref="INetworkController"/> (SCReAM), and raises
/// the resulting <see cref="BitrateEstimate"/> so the media layer can retune the encoder and pacer. With no
/// controller (a pure receiver) only the feedback-generating half runs.
/// </summary>
public sealed partial class PeerConnection
{
    private readonly INetworkController? _controller;
    private readonly Lock _ccGate = new();
    private readonly Dictionary<long, SentPacketInfo> _sentPackets = []; // key = (ssrc << 16) | seq
    private readonly Dictionary<uint, CcfbReceiveTracker> _ccfbTrackers = [];
    private readonly List<PacketResult> _feedbackScratch = [];
    private readonly List<CcfbStreamReport> _ccfbScratch = [];
    private readonly List<CcfbStreamReport> _reportScratch = [];

    // Feedback arrives on every ICE socket's receive loop (the selected path and warm standbys) while the
    // process timer also drives the controller; this serialises both, and the parse scratch with them.
    // Taken before _ccGate, never after.
    private readonly Lock _feedbackGate = new();
    private ITimer? _ccfbTimer;
    private ITimer? _processTimer;
    private double _lossRate;

    private static readonly TimeSpan ProcessInterval = TimeSpan.FromMilliseconds(25); // how often the sender's controller self-adapts

    // Feedback timing, as libwebrtc's: a report goes once a frame's last packet (the marker) has arrived, or
    // 25 ms after the first unreported arrival when no marker comes; reports are at least 25 ms apart and
    // at most 250 ms, and together stay under 500 kbit/s.
    private const long MinFeedbackSpacingMicros = 25_000;
    private const long MaxFeedbackSpacingMicros = 250_000;
    private const long MarkerWaitMicros = 25_000;
    private const double MaxFeedbackBytesPerMicro = 500_000 / 8.0 / 1_000_000;

    // Bytes a feedback packet costs beyond its RTCP (UDP, IP, SRTCP), for the rate limit.
    private const int FeedbackOverheadBytes = 42;

    // One CCFB packet's RTCP stays under this, so it fits a path MTU with SRTCP and the transport around
    // it; a longer report is split across packets (section 3.1).
    private const int MaxFeedbackPacketBytes = 1200;

    // The most packets one report block covers, so a block alone fits a packet.
    private const int MaxReportRun = 512;

    private long _firstUnreportedArrivalMicros = -1;
    private bool _markerSinceFeedback;
    private long _nextFeedbackMicros;
    private long _lastFeedbackMicros = -1;
    private double _feedbackDebtBytes;

    /// <summary>
    /// Raised when the send-side congestion controller produces a new estimate (target bitrate + pacing rate).
    /// The media layer retunes the encoder and the pacer from it. Never raised when no controller is attached.
    /// </summary>
    public event Action<BitrateEstimate>? BitrateEstimateChanged;

    /// <summary>The controller's latest estimate, or a zero estimate when no controller is attached.</summary>
    public BitrateEstimate CurrentBitrateEstimate => _controller?.CurrentEstimate ?? default;

    // Lifetime loss-recovery counters (monotonic; cross-thread, so read/written via Interlocked). The media
    // layer logs per-second deltas of these to trace where packets/frames are lost and how much recovery helps.
    private long _mediaPacketsSent;
    private long _rtxPacketsSent;
    private long _nackSequencesRequested;
    private long _rtxPacketsRecovered;
    private long _keyframeRequestsSent;

    /// <summary>
    /// A snapshot of the transport's loss-recovery counters (lifetime totals): primary RTP packets sent, RTX
    /// retransmissions sent, NACK'd sequences requested, RTX packets recovered on receive, and keyframe (PLI)
    /// requests sent. The send-side counters are meaningful on a sender, the recover/request counters on a
    /// receiver. Logged as per-second deltas by the media layer to localise loss in the pipeline.
    /// </summary>
    public TransportLossStats CurrentLossStats =>
        new(
            Volatile.Read(ref _mediaPacketsSent),
            Volatile.Read(ref _rtxPacketsSent),
            Volatile.Read(ref _nackSequencesRequested),
            Volatile.Read(ref _rtxPacketsRecovered),
            Volatile.Read(ref _keyframeRequestsSent)
        );

    /// <summary>
    /// The aggregated transport health (loss + RTT + rate). Meaningful on the sending side once feedback has
    /// arrived; a zeroed snapshot otherwise.
    /// </summary>
    public TransportHealthMetrics CurrentHealth
    {
        get
        {
            BitrateEstimate e = CurrentBitrateEstimate;
            return new TransportHealthMetrics(
                _lossRate,
                e.SmoothedRttMicros,
                e.BaseRttMicros,
                e.TargetBitrateBps,
                e.PacingRateBps
            );
        }
    }

    // Microseconds on the connection's monotonic clock, counted from its creation.
    // A new estimate retunes the pacer here, so media and repairs follow it, then the media layer.
    private void OnEstimate(BitrateEstimate estimate)
    {
        _pacer.BitsPerSecond = Math.Max(0, estimate.PacingRateBps);
        BitrateEstimateChanged?.Invoke(estimate);
    }

    private long NowMicros() => _time.GetElapsedTime(_origin).Ticks / TimeSpan.TicksPerMicrosecond;

    private static long Key(uint ssrc, ushort seq) => ((long)ssrc << 16) | seq;

    // Send side: remember each transmitted packet for later correlation with feedback, and tell the controller.
    private void RecordSent(uint ssrc, ushort seq, int sizeBytes, long nowMicros)
    {
        var info = new SentPacketInfo(seq, sizeBytes, nowMicros);
        lock (_ccGate)
        {
            _sentPackets[Key(ssrc, seq)] = info;

            // Bound memory: drop anything older than ~2 s. Cheap, runs only as packets are sent.
            if (_sentPackets.Count > 4096)
            {
                long cutoff = nowMicros - 2_000_000;
                foreach (
                    long k in _sentPackets
                        .Where(kv => kv.Value.SendTimeMicros < cutoff)
                        .Select(kv => kv.Key)
                        .ToArray()
                )
                {
                    _sentPackets.Remove(k);
                }
            }
        }

        _controller?.OnPacketSent(info);
    }

    // Receive side: note the arrival (time and ECN mark) of an RTP packet for the next CCFB report, and send
    // the report when a frame has ended and the spacing allows.
    private void RecordArrival(uint ssrc, ushort seq, long nowMicros, byte ecn, bool marker)
    {
        bool due;
        lock (_ccGate)
        {
            if (!_ccfbTrackers.TryGetValue(ssrc, out CcfbReceiveTracker? tracker))
            {
                tracker = new CcfbReceiveTracker();
                _ccfbTrackers[ssrc] = tracker;
            }

            tracker.OnPacket(seq, nowMicros, ecn);
            if (_firstUnreportedArrivalMicros < 0)
            {
                _firstUnreportedArrivalMicros = nowMicros;
            }

            _markerSinceFeedback |= marker;
            due = FeedbackDue(nowMicros);
        }

        if (due)
        {
            SendCongestionFeedback();
        }
    }

    // Under _ccGate: whether a report should go now.
    private bool FeedbackDue(long nowMicros) =>
        _ccfbNegotiated
        && _firstUnreportedArrivalMicros >= 0
        && nowMicros >= _nextFeedbackMicros
        && (_markerSinceFeedback || nowMicros - _firstUnreportedArrivalMicros >= MarkerWaitMicros);

    private void StartCongestionTimers()
    {
        StartReports();
        // The feedback timer runs on both peers (whoever receives media sends CCFB). The process timer only
        // matters where a controller consumes feedback, but starting it unconditionally is harmless.
        _ccfbTimer ??= _time.CreateTimer(
            static s =>
            {
                var pc = (PeerConnection)s!;
                pc.SendCongestionFeedbackIfDue();
                pc.ProcessNackResends();
            },
            this,
            TimeSpan.FromMicroseconds(MinFeedbackSpacingMicros),
            TimeSpan.FromMicroseconds(MinFeedbackSpacingMicros)
        );
        if (_controller is not null)
        {
            _processTimer ??= _time.CreateTimer(
                static s => ((PeerConnection)s!).RunProcessInterval(),
                this,
                ProcessInterval,
                ProcessInterval
            );
        }
    }

    private void RunProcessInterval()
    {
        if (_controller is null)
        {
            return;
        }

        BitrateEstimate estimate;
        lock (_feedbackGate)
        {
            estimate = _controller.OnProcessInterval(NowMicros());
        }

        OnEstimate(estimate);
    }

    private void SendCongestionFeedbackIfDue()
    {
        bool due;
        lock (_ccGate)
        {
            due = FeedbackDue(NowMicros());
        }

        if (due)
        {
            SendCongestionFeedback();
        }
    }

    // One report: every stream's blocks since the last, in as many CCFB packets as fit the size limit, each
    // SRTP-protected and behind the compound prefix when that is needed.
    private void SendCongestionFeedback()
    {
        if (_srtp is not { } srtp || _iceAgent is not { } agent)
        {
            return;
        }

        long now;
        lock (_ccGate)
        {
            now = NowMicros();
            if (!FeedbackDue(now))
            {
                return;
            }

            _reportScratch.Clear();
            foreach ((uint ssrc, CcfbReceiveTracker tracker) in _ccfbTrackers)
            {
                tracker.AddToFeedback(ssrc, now, MaxReportRun, _reportScratch);
            }

            _firstUnreportedArrivalMicros = -1;
            _markerSinceFeedback = false;
            int bytes = 0;
            foreach (CcfbStreamReport report in _reportScratch)
            {
                bytes += BlockLength(report);
            }

            ScheduleNextFeedback(now, bytes + 12 + FeedbackOverheadBytes);
            if (_reportScratch.Count == 0)
            {
                return;
            }

            // The middle 32 bits of the NTP time the sender reports carry (RFC 8888 section 3.1).
            uint reportTimestamp = (uint)(NtpAt(now) >> 16);
            int start = 0;
            while (start < _reportScratch.Count)
            {
                int count = 0;
                int length = 12;
                while (
                    start + count < _reportScratch.Count
                    && (
                        count == 0
                        || length + BlockLength(_reportScratch[start + count])
                            <= MaxFeedbackPacketBytes
                    )
                )
                {
                    length += BlockLength(_reportScratch[start + count]);
                    count++;
                }

                SendFeedbackPacket(
                    srtp,
                    agent,
                    _reportScratch.GetRange(start, count),
                    reportTimestamp
                );
                start += count;
            }
        }
    }

    private void SendFeedbackPacket(
        SrtpSession srtp,
        Ice.IceAgent agent,
        List<CcfbStreamReport> reports,
        uint reportTimestamp
    )
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(
            FeedbackPrefixCapacity
                + MaxFeedbackPacketBytes
                + 64
                + SrtpSession.MaxRtcpProtectionOverhead
        );
        try
        {
            int prefix = WriteFeedbackPrefix(buffer);
            int length =
                prefix
                + Ccfb.Build(buffer.AsSpan(prefix), _rtcpSenderSsrc, reports, reportTimestamp);
            int protectedLength = srtp.ProtectRtcp(buffer, length);
            _ = agent.SendAsync(buffer.AsMemory(0, protectedLength));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFeedbackFailed(_logger, ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // A report block's bytes: SSRC, begin and count, then two bytes a packet padded to 32 bits.
    private static int BlockLength(CcfbStreamReport report) =>
        8 + (((report.Metrics.Count + 1) / 2) * 4);

    // Under _ccGate: when the next report may go, from the bytes reports have cost against the rate limit.
    private void ScheduleNextFeedback(long nowMicros, int bytes)
    {
        if (_lastFeedbackMicros >= 0)
        {
            double paid = (nowMicros - _lastFeedbackMicros) * MaxFeedbackBytesPerMicro;
            _feedbackDebtBytes = Math.Max(0, _feedbackDebtBytes - paid);
        }

        _lastFeedbackMicros = nowMicros;
        _feedbackDebtBytes += bytes;
        _nextFeedbackMicros =
            nowMicros
            + Math.Clamp(
                (long)(_feedbackDebtBytes / MaxFeedbackBytesPerMicro),
                MinFeedbackSpacingMicros,
                MaxFeedbackSpacingMicros
            );
    }

    // Send side: parse inbound CCFB, correlate each reported sequence with what we sent, and feed the controller.
    private void OnCongestionFeedback(ReadOnlySpan<byte> rtcp)
    {
        if (_controller is null)
        {
            return;
        }

        BitrateEstimate? estimate;
        lock (_feedbackGate)
        {
            estimate = ApplyCongestionFeedback(rtcp, _controller);
        }

        if (estimate is { } changed)
        {
            OnEstimate(changed);
        }
    }

    // Correlates one CCFB report with what was sent and feeds the controller; null when the report
    // matched nothing. Runs under _feedbackGate.
    private BitrateEstimate? ApplyCongestionFeedback(
        ReadOnlySpan<byte> rtcp,
        INetworkController controller
    )
    {
        _ccfbScratch.Clear();
        if (!Ccfb.TryParse(rtcp, out _, out uint reportTimestamp, _ccfbScratch))
        {
            return null;
        }

        long reportMicros = (long)reportTimestamp * 1_000_000 / 65536;
        _feedbackScratch.Clear();
        lock (_ccGate)
        {
            foreach (CcfbStreamReport stream in _ccfbScratch)
            {
                for (int i = 0; i < stream.Metrics.Count; i++)
                {
                    ushort seq = (ushort)(stream.BeginSequence + i);
                    if (!_sentPackets.TryGetValue(Key(stream.Ssrc, seq), out SentPacketInfo sent))
                    {
                        continue;
                    }

                    CcfbMetric metric = stream.Metrics[i];
                    long recvMicros = -1;
                    if (metric.Received && metric.ArrivalTimeOffset != Ccfb.ArrivalTimeUnknown)
                    {
                        // Receiver-frame arrival; the controller works on send-vs-arrival deltas, so the
                        // constant clock offset between peers cancels.
                        recvMicros =
                            reportMicros - ((long)metric.ArrivalTimeOffset * 1_000_000 / 1024);
                    }

                    _feedbackScratch.Add(
                        new PacketResult(
                            seq,
                            sent.SizeBytes,
                            sent.SendTimeMicros,
                            recvMicros,
                            metric.Ecn
                        )
                    );
                }
            }
        }

        if (_feedbackScratch.Count == 0)
        {
            return null;
        }

        // Rolling loss rate over the reported window (EWMA), for the health model.
        int lost = 0;
        foreach (PacketResult r in _feedbackScratch)
        {
            if (!r.Received)
            {
                lost++;
            }
        }

        double sample = (double)lost / _feedbackScratch.Count;
        _lossRate = _lossRate <= 0 ? sample : (_lossRate * 0.8) + (sample * 0.2);

        return controller.OnFeedback(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_feedbackScratch),
            NowMicros()
        );
    }

    [Microsoft.Extensions.Logging.LoggerMessage(
        EventId = 1158,
        Level = Microsoft.Extensions.Logging.LogLevel.Debug,
        Message = "A congestion feedback report could not be sent"
    )]
    private static partial void LogFeedbackFailed(
        Microsoft.Extensions.Logging.ILogger logger,
        Exception exception
    );

    private void DisposeCongestion()
    {
        _ccfbTimer?.Dispose();
        _processTimer?.Dispose();
    }
}
