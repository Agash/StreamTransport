using System.Buffers;
using System.Diagnostics;
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
    private readonly Dictionary<long, SentPacketInfo> _sentPackets = [];      // key = (ssrc << 16) | seq
    private readonly Dictionary<uint, Dictionary<ushort, ArrivalInfo>> _arrivals = []; // ssrc -> (seq -> arrival µs + ECN)
    private readonly List<PacketResult> _feedbackScratch = [];
    private readonly List<CcfbStreamReport> _ccfbScratch = [];
    private Timer? _ccfbTimer;
    private Timer? _processTimer;
    private double _lossRate;

    private const int FeedbackIntervalMs = 50;   // how often the receiver emits CCFB
    private const int ProcessIntervalMs = 25;     // how often the sender's controller self-adapts
    private const int MaxReportRun = 256;         // cap a CCFB run so one packet stays small

    /// <summary>
    /// Raised when the send-side congestion controller produces a new estimate (target bitrate + pacing rate).
    /// The media layer retunes the encoder and the pacer from it. Never raised when no controller is attached.
    /// </summary>
    public event Action<BitrateEstimate>? BitrateEstimateChanged;

    /// <summary>The controller's latest estimate, or a zero estimate when no controller is attached.</summary>
    public BitrateEstimate CurrentBitrateEstimate => _controller?.CurrentEstimate ?? default;

    /// <summary>
    /// The aggregated transport health (loss + RTT + rate). Meaningful on the sending side once feedback has
    /// arrived; a zeroed snapshot otherwise.
    /// </summary>
    public TransportHealthMetrics CurrentHealth
    {
        get
        {
            BitrateEstimate e = CurrentBitrateEstimate;
            return new TransportHealthMetrics(_lossRate, e.SmoothedRttMicros, e.BaseRttMicros, e.TargetBitrateBps, e.PacingRateBps);
        }
    }

    private static long NowMicros() => Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;

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
                foreach (long k in _sentPackets.Where(kv => kv.Value.SendTimeMicros < cutoff).Select(kv => kv.Key).ToArray())
                {
                    _sentPackets.Remove(k);
                }
            }
        }

        _controller?.OnPacketSent(info);
    }

    // Receive side: stamp the arrival (time + ECN mark) of a media RTP packet for the next CCFB report. When a
    // sequence number recurs, the CE mark is sticky - once the network marked a packet congested we report it.
    private void RecordArrival(uint ssrc, ushort seq, long nowMicros, byte ecn)
    {
        lock (_ccGate)
        {
            if (!_arrivals.TryGetValue(ssrc, out Dictionary<ushort, ArrivalInfo>? perSsrc))
            {
                perSsrc = [];
                _arrivals[ssrc] = perSsrc;
            }

            byte mark = ecn;
            if (perSsrc.TryGetValue(seq, out ArrivalInfo existing) && existing.Ecn == 0x03)
            {
                mark = 0x03;
            }

            perSsrc[seq] = new ArrivalInfo(nowMicros, mark);
        }
    }

    private void StartCongestionTimers()
    {
        // The feedback timer runs on both peers (whoever receives media sends CCFB). The process timer only
        // matters where a controller consumes feedback, but starting it unconditionally is harmless.
        _ccfbTimer ??= new Timer(
            static s =>
            {
                var pc = (PeerConnection)s!;
                pc.SendCongestionFeedback();
                pc.ProcessNackResends();
            },
            this,
            FeedbackIntervalMs,
            FeedbackIntervalMs);
        if (_controller is not null)
        {
            _processTimer ??= new Timer(static s => ((PeerConnection)s!).RunProcessInterval(), this, ProcessIntervalMs, ProcessIntervalMs);
        }
    }

    private void RunProcessInterval()
    {
        if (_controller is null)
        {
            return;
        }

        BitrateEstimate estimate = _controller.OnProcessInterval(NowMicros());
        BitrateEstimateChanged?.Invoke(estimate);
    }

    // Build one CCFB packet from the arrivals seen since the last report and send it SRTP-protected.
    private void SendCongestionFeedback()
    {
        if (_srtp is not { } srtp || _iceAgent is not { } agent)
        {
            return;
        }

        long now = NowMicros();
        _ccfbScratch.Clear();
        lock (_ccGate)
        {
            foreach ((uint ssrc, Dictionary<ushort, ArrivalInfo> perSsrc) in _arrivals)
            {
                if (perSsrc.Count == 0)
                {
                    continue;
                }

                // Run from the earliest-arriving sequence across the window (16-bit arithmetic handles wrap),
                // marking each sequence received-or-not with its arrival-time offset before the report time.
                ushort begin = EarliestSequence(perSsrc);
                ushort highest = HighestSequence(perSsrc, begin);
                int run = Math.Min(MaxReportRun, (ushort)(highest - begin) + 1);

                var metrics = new CcfbMetric[run];
                for (int i = 0; i < run; i++)
                {
                    ushort seq = (ushort)(begin + i);
                    if (perSsrc.TryGetValue(seq, out ArrivalInfo arrival))
                    {
                        long offset1024 = (now - arrival.Micros) * 1024 / 1_000_000;
                        ushort ato = offset1024 is >= 0 and < Ccfb.ArrivalTimeUnknown ? (ushort)offset1024 : Ccfb.ArrivalTimeUnknown;
                        metrics[i] = new CcfbMetric(true, arrival.Ecn, ato);
                    }
                    else
                    {
                        metrics[i] = new CcfbMetric(false, 0, 0);
                    }
                }

                _ccfbScratch.Add(new CcfbStreamReport(ssrc, begin, metrics));
                perSsrc.Clear();
            }
        }

        if (_ccfbScratch.Count == 0)
        {
            return;
        }

        uint reportTimestamp = (uint)(now * 65536 / 1_000_000);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1300 + SrtpSession.RtcpProtectionOverhead);
        try
        {
            int length = Ccfb.Build(buffer, _rtcpSenderSsrc, _ccfbScratch, reportTimestamp);
            int protectedLength = srtp.ProtectRtcp(buffer, length);
            _ = agent.SendAsync(buffer.AsMemory(0, protectedLength));
        }
        catch (Exception)
        {
            // Feedback is best-effort; a send race during teardown is benign.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Send side: parse inbound CCFB, correlate each reported sequence with what we sent, and feed the controller.
    private void OnCongestionFeedback(ReadOnlySpan<byte> rtcp)
    {
        if (_controller is null)
        {
            return;
        }

        _ccfbScratch.Clear();
        if (!Ccfb.TryParse(rtcp, out _, out uint reportTimestamp, _ccfbScratch))
        {
            return;
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
                        recvMicros = reportMicros - ((long)metric.ArrivalTimeOffset * 1_000_000 / 1024);
                    }

                    _feedbackScratch.Add(new PacketResult(seq, sent.SizeBytes, sent.SendTimeMicros, recvMicros, metric.Ecn));
                }
            }
        }

        if (_feedbackScratch.Count == 0)
        {
            return;
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

        BitrateEstimate estimate = _controller.OnFeedback(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_feedbackScratch), NowMicros());
        BitrateEstimateChanged?.Invoke(estimate);
    }

    // The sequence with the smallest 16-bit distance ahead of the earliest-arriving packet, i.e. the window start.
    private static ushort EarliestSequence(Dictionary<ushort, ArrivalInfo> perSsrc)
    {
        ushort earliest = 0;
        long best = long.MaxValue;
        foreach ((ushort seq, ArrivalInfo info) in perSsrc)
        {
            if (info.Micros < best)
            {
                best = info.Micros;
                earliest = seq;
            }
        }

        return earliest;
    }

    private static ushort HighestSequence(Dictionary<ushort, ArrivalInfo> perSsrc, ushort begin)
    {
        ushort highestDistance = 0;
        foreach (ushort seq in perSsrc.Keys)
        {
            ushort distance = (ushort)(seq - begin);
            if (distance < MaxReportRun && distance > highestDistance)
            {
                highestDistance = distance;
            }
        }

        return (ushort)(begin + highestDistance);
    }

    /// <summary>A media packet's recorded arrival: monotonic µs and the 2-bit ECN mark read off the wire.</summary>
    private readonly record struct ArrivalInfo(long Micros, byte Ecn);

    private void DisposeCongestion()
    {
        _ccfbTimer?.Dispose();
        _processTimer?.Dispose();
    }
}
