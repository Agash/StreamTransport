using System.Buffers;
using System.Security.Cryptography;
using Agash.StreamTransport.WebRtc.Rtcp;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Sdp;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.WebRtc;

// RTCP reports (RFC 3550 section 6, RFC 8834 section 4.1): a compound report about once a second, a sender
// report for each source this endpoint sends and a receiver report otherwise, with a reception block for
// each source it receives and the CNAME; feedback packets go out alone only where both sides listed
// rtcp-rsize (RFC 5506), otherwise behind a receiver report and the CNAME. One NTP clock stamps the sender
// reports and the congestion-control feedback (RFC 8888 section 3.1).
public sealed partial class PeerConnection
{
    // The reporting interval's mean; each interval is drawn from half to one and a half times it (RFC 3550
    // section 6.3.1), so endpoints that started together do not report in lockstep.
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(1);

    // Room the receiver report and CNAME take in front of a feedback packet.
    private const int FeedbackPrefixCapacity = 64;

    // A per-connection canonical name, 96 random bits (RFC 7022 section 4.2), so it ties this endpoint's
    // streams together without identifying it.
    private readonly string _cname = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12));
    private readonly Lock _reportGate = new();
    private readonly Dictionary<uint, SendStatistics> _sendStatistics = [];
    private readonly Dictionary<uint, RtpReceiveStatistics> _receiveStatistics = [];
    private ulong _ntpOrigin;
    private ITimer? _reportTimer;
    private volatile bool _rtcpReducedSize;
    private volatile bool _ccfbNegotiated;
    private long _rtcpRoundTripMicros;

    /// <summary>
    /// Raised for each sender report from the peer, with the source, its NTP timestamp and the RTP
    /// timestamp that corresponds to it (RFC 3550 section 6.4.1): the mapping a receiver aligns a stream's
    /// RTP clock to the sender's wall clock with, when packets carry no capture time.
    /// </summary>
    public event Action<uint, ulong, uint>? SenderReportReceived;

    /// <summary>
    /// The round trip the peer's reception reports measured against this endpoint's sender reports
    /// (RFC 3550 section 6.4.1), or zero before one arrived.
    /// </summary>
    public TimeSpan ReportedRoundTripTime =>
        TimeSpan.FromTicks(
            Interlocked.Read(ref _rtcpRoundTripMicros) * TimeSpan.TicksPerMicrosecond
        );

    // NTP time (RFC 3550 section 4) at a point on the connection's monotonic clock: the wall clock read once,
    // advanced by the monotonic clock, so reports never jump with wall-clock corrections.
    private ulong NtpAt(long micros)
    {
        if (_ntpOrigin == 0)
        {
            _ntpOrigin = ToNtp(_time.GetUtcNow()) - MicrosToNtp(NowMicros());
        }

        return _ntpOrigin + MicrosToNtp(micros);
    }

    private static ulong ToNtp(DateTimeOffset time)
    {
        long micros = (time - new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero)).Ticks / 10;
        return MicrosToNtp(micros);
    }

    private static ulong MicrosToNtp(long micros) =>
        ((ulong)(micros / 1_000_000) << 32) | ((ulong)(micros % 1_000_000) << 32) / 1_000_000;

    // Notes a sent packet for the sender report: counts, and the newest RTP timestamp with when it left.
    private void RecordSentForReports(
        uint ssrc,
        byte payloadType,
        uint rtpTimestamp,
        int payloadLength,
        long nowMicros
    )
    {
        lock (_reportGate)
        {
            if (!_sendStatistics.TryGetValue(ssrc, out SendStatistics? statistics))
            {
                statistics = new SendStatistics(ClockRateOf(payloadType, local: true));
                _sendStatistics[ssrc] = statistics;
            }

            statistics.Packets++;
            statistics.Octets += payloadLength;
            statistics.RtpTimestamp = rtpTimestamp;
            statistics.SentMicros = nowMicros;
        }
    }

    // Notes a received media packet for the reception report.
    private void RecordReceivedForReports(RtpHeader header, long nowMicros)
    {
        lock (_reportGate)
        {
            if (!_receiveStatistics.TryGetValue(header.Ssrc, out RtpReceiveStatistics? statistics))
            {
                statistics = new RtpReceiveStatistics(
                    ClockRateOf(header.PayloadType, local: false)
                );
                _receiveStatistics[header.Ssrc] = statistics;
            }

            statistics.OnPacket(header.SequenceNumber, header.Timestamp, nowMicros);
        }
    }

    // A payload type's clock rate as negotiated; video's 90 kHz for one not listed (FlexFEC, unknown).
    private int ClockRateOf(byte payloadType, bool local)
    {
        foreach (NegotiatedMediaInfo media in NegotiatedMedia)
        {
            foreach (SdpCodec codec in local ? media.Codecs : media.RemoteCodecs)
            {
                if (codec.PayloadType == payloadType)
                {
                    return codec.ClockRate;
                }
            }
        }

        return 90_000;
    }

    // The peer's sender reports start the round trip it measures; its reception blocks about this
    // endpoint's sources close the ones this endpoint started.
    private void OnReports(ReadOnlySpan<byte> rtcp)
    {
        long now = NowMicros();
        foreach (RtcpElement element in RtcpCompound.Enumerate(rtcp))
        {
            ReadOnlySpan<byte> body = element.Body;
            int blocksAt;
            if (element.PacketType == RtcpPacketType.SenderReport && body.Length >= 24)
            {
                uint ssrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body);
                ulong ntp = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(body[4..]);
                uint rtpTimestamp = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                    body[12..]
                );
                SenderReportReceived?.Invoke(ssrc, ntp, rtpTimestamp);
                lock (_reportGate)
                {
                    if (_receiveStatistics.TryGetValue(ssrc, out RtpReceiveStatistics? statistics))
                    {
                        statistics.OnSenderReport(ntp, now);
                    }
                }

                blocksAt = 24;
            }
            else if (element.PacketType == RtcpPacketType.ReceiverReport && body.Length >= 4)
            {
                blocksAt = 4;
            }
            else
            {
                continue;
            }

            for (
                int i = 0;
                i < element.ReportCount
                    && blocksAt + ((i + 1) * RtcpReportBlock.Length) <= body.Length;
                i++
            )
            {
                var block = RtcpReportBlock.Read(body[(blocksAt + (i * RtcpReportBlock.Length))..]);
                if (!IsOwnSource(block.Ssrc))
                {
                    continue;
                }

                // RTT = arrival - LSR - DLSR, all in the middle 32 bits of NTP time (1/65536 s).
                if (block.LastSenderReport != 0)
                {
                    uint arrival = (uint)(NtpAt(now) >> 16);
                    uint roundTrip =
                        arrival - block.LastSenderReport - block.DelaySinceLastSenderReport;
                    if (roundTrip < 0x8000_0000)
                    {
                        Interlocked.Exchange(
                            ref _rtcpRoundTripMicros,
                            (long)roundTrip * 1_000_000 / 65536
                        );
                    }
                }

                BreakerOnReport(
                    block.Ssrc,
                    block.ExtendedHighestSequence,
                    block.FractionLost,
                    ReportedRoundTripTime
                );
            }
        }
    }

    private bool IsOwnSource(uint ssrc)
    {
        lock (_reportGate)
        {
            return _sendStatistics.ContainsKey(ssrc);
        }
    }

    private void StartReports()
    {
        _reportTimer ??= _time.CreateTimer(
            static s => ((PeerConnection)s!).SendReport(goodbye: false),
            this,
            NextReportDelay(),
            Timeout.InfiniteTimeSpan
        );
    }

    private static TimeSpan NextReportDelay() =>
        ReportInterval * (0.5 + Random.Shared.NextDouble());

    // A compound report: sender reports (or a receiver report), reception blocks, the CNAME, and a BYE when
    // the connection closes.
    private void SendReport(bool goodbye)
    {
        if (!goodbye)
        {
            _ = _reportTimer?.Change(NextReportDelay(), Timeout.InfiniteTimeSpan);
        }

        if (_srtp is not { } srtp || _iceAgent is not { } agent)
        {
            return;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(1400);
        try
        {
            int length = WriteReports(buffer, goodbye);
            int protectedLength = srtp.ProtectRtcp(buffer, length);
            _ = agent.SendAsync(buffer.AsMemory(0, protectedLength));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogReportFailed(_logger, ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private int WriteReports(Span<byte> destination, bool goodbye)
    {
        long now = NowMicros();
        ulong ntp = NtpAt(now);
        List<RtcpReportBlock> blocks = [];
        List<(uint Ssrc, SendStatistics Statistics)> senders = [];
        lock (_reportGate)
        {
            foreach ((uint ssrc, RtpReceiveStatistics statistics) in _receiveStatistics)
            {
                if (blocks.Count < 31 && statistics.Report(ssrc, now) is { } block)
                {
                    blocks.Add(block);
                }
            }

            foreach ((uint ssrc, SendStatistics statistics) in _sendStatistics)
            {
                senders.Add((ssrc, statistics));
            }
        }

        int offset = 0;
        if (senders.Count == 0)
        {
            offset += new RtcpReceiverReport(_rtcpSenderSsrc, blocks).Write(destination);
        }
        else
        {
            // The reception blocks ride on the first sender report; the others report only themselves.
            for (int i = 0; i < senders.Count; i++)
            {
                (uint ssrc, SendStatistics statistics) = senders[i];
                uint rtpTimestamp =
                    statistics.RtpTimestamp
                    + (uint)((now - statistics.SentMicros) * statistics.ClockRate / 1_000_000);
                offset += new RtcpSenderReport(
                    ssrc,
                    ntp,
                    rtpTimestamp,
                    (uint)statistics.Packets,
                    (uint)statistics.Octets,
                    i == 0 ? blocks : []
                ).Write(destination[offset..]);
            }
        }

        Span<uint> sources = stackalloc uint[Math.Min(31, senders.Count + 1)];
        int count = 0;
        sources[count++] = _rtcpSenderSsrc;
        foreach ((uint ssrc, _) in senders)
        {
            if (count < sources.Length && ssrc != _rtcpSenderSsrc)
            {
                sources[count++] = ssrc;
            }
        }

        offset += RtcpSourceDescription.WriteCname(destination[offset..], sources[..count], _cname);
        if (goodbye)
        {
            offset += RtcpSourceDescription.WriteGoodbye(destination[offset..], sources[..count]);
        }

        return offset;
    }

    // What precedes a feedback packet: nothing where reduced-size RTCP was agreed, else an empty receiver
    // report and the CNAME, which make it a valid compound packet.
    private int WriteFeedbackPrefix(Span<byte> destination)
    {
        if (_rtcpReducedSize)
        {
            return 0;
        }

        int offset = new RtcpReceiverReport(_rtcpSenderSsrc, []).Write(destination);
        offset += RtcpSourceDescription.WriteCname(
            destination[offset..],
            [_rtcpSenderSsrc],
            _cname
        );
        return offset;
    }

    // What both sides agreed for RTCP: reduced-size packets and congestion-control feedback, each when every
    // section of the remote description lists it (this endpoint offers both, and answers what was offered).
    private void NoteRtcpCapabilities(SdpDescription remote)
    {
        bool reducedSize = remote.Media.Count > 0;
        bool feedback = remote.Media.Count > 0;
        foreach (SdpMediaDescription media in remote.Media)
        {
            reducedSize &= media.RtcpReducedSize;
            feedback &= media.CongestionControlFeedback;
        }

        _rtcpReducedSize = reducedSize;
        _ccfbNegotiated = feedback;
        NoteEcn(remote);
    }

    private async ValueTask StopReportsAsync()
    {
        if (Interlocked.Exchange(ref _reportTimer, null) is { } timer)
        {
            await timer.DisposeAsync().ConfigureAwait(false);
            SendReport(goodbye: true);
        }
    }

    private sealed class SendStatistics(int clockRate)
    {
        public int ClockRate { get; } = clockRate;

        public long Packets { get; set; }

        public long Octets { get; set; }

        public uint RtpTimestamp { get; set; }

        public long SentMicros { get; set; }
    }

    [LoggerMessage(
        EventId = 1157,
        Level = LogLevel.Debug,
        Message = "An RTCP report could not be sent"
    )]
    private static partial void LogReportFailed(ILogger logger, Exception exception);
}
