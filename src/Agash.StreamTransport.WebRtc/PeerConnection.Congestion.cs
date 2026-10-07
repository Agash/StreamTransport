using System.Buffers;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.WebRtc.Rtcp;
using Agash.StreamTransport.WebRtc.Srtp;

namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// Congestion-control wiring for <see cref="PeerConnection"/>, the RTP adapter onto the transport-neutral
/// adaptation layer: the receive side records RTP arrivals and sends RFC 8888 congestion-control feedback;
/// the send side numbers every packet it sends, maps inbound feedback onto those numbers for a
/// <see cref="DeliveryTracker"/>, feeds the resolved outcomes to the <see cref="ICongestionController"/>, and
/// raises the resulting <see cref="CapacityEstimate"/> so the pacer and the media layer follow it. With no
/// controller (a pure receiver) only the feedback-generating half runs.
/// </summary>
public sealed partial class PeerConnection
{
    private readonly ICongestionController? _controller;
    private readonly Lock _ccGate = new();

    // Every sent packet's number, by SSRC and RTP sequence number, for mapping feedback onto them.
    private readonly Dictionary<long, long> _sentIds = [];
    private readonly DeliveryTracker _delivery = new();
    private readonly List<PacketReport> _packetReports = [];
    private readonly List<PacketObservation> _observations = [];
    private long _nextPacketId;
    private long _reportTimestamp = -1;
    private readonly Dictionary<uint, CcfbReceiveTracker> _ccfbTrackers = [];
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

    // Feedback timing (SCReAMv2 section 5, after libwebrtc's): a report goes once a frame's last packet (the
    // marker) has arrived, once 16 packets are unreported, or 25 ms after the first unreported arrival;
    // reports are at least 10 ms apart and at most 250 ms, and together stay under 500 kbit/s.
    private const long MinFeedbackSpacingMicros = 10_000;
    private const int PacketsPerReport = 16;
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
    private int _unreportedPackets;
    private bool _markerSinceFeedback;
    private long _nextFeedbackMicros;
    private long _lastFeedbackMicros = -1;
    private double _feedbackDebtBytes;

    /// <summary>
    /// Raised when the congestion controller produces a new estimate. The connection's pacer has already
    /// followed it; the media layer retunes the encoder from it. Never raised without a controller.
    /// </summary>
    public event Action<CapacityEstimate>? CapacityChanged;

    /// <summary>The controller's latest estimate, or a zero estimate when no controller is attached.</summary>
    public CapacityEstimate CurrentCapacity => _controller?.Current ?? default;

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
            CapacityEstimate e = CurrentCapacity;
            return new TransportHealthMetrics(
                _lossRate,
                e.SmoothedRoundTrip.Ticks / TimeSpan.TicksPerMicrosecond,
                e.MinimumRoundTrip.Ticks / TimeSpan.TicksPerMicrosecond,
                e.TargetBitsPerSecond,
                e.PacingBitsPerSecond
            );
        }
    }

    // Microseconds on the connection's monotonic clock, counted from its creation.
    // A new estimate retunes the pacer here, so media and repairs follow it, then the media layer.
    private void OnEstimate(CapacityEstimate estimate)
    {
        estimate = ApplyBreakerCap(estimate);
        _pacer.BitsPerSecond = Math.Max(0, estimate.PacingBitsPerSecond);

        // Feedback may have opened the send window.
        _pacer.Wake();
        CapacityChanged?.Invoke(estimate);
    }

    private TimeSpan Now => _time.GetElapsedTime(_origin);

    // The pacer's gate: the controller's send window.
    private bool MayTransmit(int bytes)
    {
        lock (_feedbackGate)
        {
            return _controller!.CanTransmit(bytes, Now);
        }
    }

    /// <summary>
    /// Notes an encoded video frame as the media layer hands it over, so the congestion controller can keep
    /// the sender queue short and leave room for frames larger than nominal.
    /// </summary>
    /// <param name="bytes">The frame's size.</param>
    public void NoteMediaFrame(int bytes)
    {
        if (_controller is not { } controller)
        {
            return;
        }

        TimeSpan queueDelay = _pacer.QueueDelay;
        lock (_feedbackGate)
        {
            controller.OnMediaFrame(bytes, queueDelay, Now);
        }
    }

    private long NowMicros() => _time.GetElapsedTime(_origin).Ticks / TimeSpan.TicksPerMicrosecond;

    private static long Key(uint ssrc, ushort seq) => ((long)ssrc << 16) | seq;

    // Send side: number each packet on the wire, remember it for feedback, tell the controller, and decide
    // its ECN codepoint.
    private EcnCodepoint RecordSent(uint ssrc, ushort seq, int sizeBytes, TrafficClass trafficClass)
    {
        SentPacket packet;
        lock (_ccGate)
        {
            long id = ++_nextPacketId;
            packet = new SentPacket(id, sizeBytes, Now, trafficClass);
            _sentIds[Key(ssrc, seq)] = id;
            _delivery.OnSent(packet);

            // A sequence number recurs after 65536 packets of one SSRC; numbers that old are forgotten.
            if ((id & 0xFFF) == 0)
            {
                foreach ((long key, long sent) in _sentIds)
                {
                    if (sent < id - 0x10000)
                    {
                        _ = _sentIds.Remove(key);
                    }
                }
            }
        }

        if (_controller is not { } controller)
        {
            return EcnCodepoint.NotEct;
        }

        lock (_feedbackGate)
        {
            controller.OnPacketSent(packet);
            if (trafficClass == TrafficClass.Probe)
            {
                NoteProbeSent(packet.Id);
            }

            return MarkForEcn(packet.Id);
        }
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
            _unreportedPackets++;
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
        && (
            _markerSinceFeedback
            || _unreportedPackets >= PacketsPerReport
            || nowMicros - _firstUnreportedArrivalMicros >= MarkerWaitMicros
        );

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
                pc.BreakerOnTick();
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

        CapacityEstimate estimate;
        lock (_feedbackGate)
        {
            TimeSpan now = Now;
            _observations.Clear();
            lock (_ccGate)
            {
                _delivery.OnTick(now, _observations);
            }

            if (_observations.Count > 0)
            {
                CheckEcn(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_observations));
                ObservePathMtu(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_observations),
                    now
                );
                _ = _controller.OnFeedback(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_observations),
                    null,
                    now
                );
            }

            estimate = _controller.OnTick(now);
        }

        OnEstimate(estimate);
        ProbePathMtu(Now);
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
            _unreportedPackets = 0;
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

        CapacityEstimate? estimate;
        lock (_feedbackGate)
        {
            estimate = ApplyCongestionFeedback(rtcp, _controller);
        }

        if (estimate is { } changed)
        {
            OnEstimate(changed);
        }
    }

    // Maps one CCFB report onto the sent packets, resolves their outcomes and feeds the controller; null
    // when the report matched nothing. Runs under _feedbackGate.
    private CapacityEstimate? ApplyCongestionFeedback(
        ReadOnlySpan<byte> rtcp,
        ICongestionController controller
    )
    {
        _ccfbScratch.Clear();
        if (!Ccfb.TryParse(rtcp, out _, out uint compactTimestamp, _ccfbScratch))
        {
            return null;
        }

        TimeSpan now = Now;
        TimeSpan? roundTrip;
        _packetReports.Clear();
        _observations.Clear();
        lock (_ccGate)
        {
            TimeSpan reportedAt = UnwrapReportTimestamp(compactTimestamp);
            foreach (CcfbStreamReport stream in _ccfbScratch)
            {
                for (int i = 0; i < stream.Metrics.Count; i++)
                {
                    ushort seq = (ushort)(stream.BeginSequence + i);
                    if (!_sentIds.TryGetValue(Key(stream.Ssrc, seq), out long id))
                    {
                        continue;
                    }

                    CcfbMetric metric = stream.Metrics[i];

                    // Received with no usable time (unknown, after the report, or over range) is still
                    // received (section 3.1).
                    TimeSpan? held =
                        metric.Received
                        && metric.ArrivalTimeOffset
                            is not Ccfb.ArrivalTimeUnknown
                                and not Ccfb.ArrivalTimeOverRange
                            ? TimeSpan.FromTicks(
                                metric.ArrivalTimeOffset * TimeSpan.TicksPerSecond / 1024
                            )
                            : null;
                    _packetReports.Add(
                        new PacketReport(
                            id,
                            metric.Received,
                            reportedAt - held,
                            (EcnCodepoint)(metric.Ecn & 0x03)
                        )
                    );
                }
            }

            roundTrip = _delivery.OnFeedback(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_packetReports),
                now,
                _observations
            );
        }

        if (_observations.Count == 0 && roundTrip is null)
        {
            return null;
        }

        CheckEcn(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_observations));
        ObservePathMtu(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_observations),
            now
        );

        // Rolling loss rate over the resolved packets (EWMA), for the health model. Path MTU probes are
        // left out: one lost to its size says nothing about the path's health.
        int resolved = 0;
        int lost = 0;
        foreach (PacketObservation observation in _observations)
        {
            if (observation.Packet.Class != TrafficClass.Probe)
            {
                resolved++;
                lost += observation.Outcome == PacketOutcome.Lost ? 1 : 0;
            }
        }

        if (resolved > 0)
        {
            double sample = (double)lost / resolved;
            _lossRate = _lossRate <= 0 ? sample : (_lossRate * 0.8) + (sample * 0.2);
        }

        return controller.OnFeedback(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_observations),
            roundTrip,
            now
        );
    }

    // The report timestamp is the middle 32 bits of NTP time, 1/65536 s, wrapping every 18 hours: extended
    // across wraps, as a time on the receiver's clock. Under _ccGate.
    private TimeSpan UnwrapReportTimestamp(uint compact)
    {
        _reportTimestamp =
            _reportTimestamp < 0
                ? compact
                : _reportTimestamp + (int)(compact - (uint)_reportTimestamp);
        return TimeSpan.FromTicks(_reportTimestamp * TimeSpan.TicksPerSecond / 65536);
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
