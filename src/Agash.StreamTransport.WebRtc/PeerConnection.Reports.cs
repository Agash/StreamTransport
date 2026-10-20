using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Agash.StreamTransport.WebRtc.Rtcp;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Sdp;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.WebRtc;

// RTCP reports (RFC 3550 section 6, RFC 8834 section 4.1): a compound report on the RTCP schedule (see
// RtcpScheduler), a sender report for each source this endpoint sends and a receiver report for its
// receiving source, with a reception block for each source it receives and the CNAME and MID of each;
// feedback packets go out alone only where both sides listed rtcp-rsize (RFC 5506), otherwise behind a
// receiver report and the CNAME. The member table counts every source heard, drops one after its BYE or
// its timeout (RFC 3550 sections 6.3.3 to 6.3.5), and one NTP clock stamps the sender reports and the
// congestion-control feedback (RFC 8888 section 3.1).
public sealed partial class PeerConnection
{
    // RFC 3550 section 6.2: what a session with no stated bandwidth is taken to have at least, as GStreamer's
    // rtpsession does; the measured media rate counts when higher.
    private const long MinimumSessionBitsPerSecond = 64_000;

    // UDP and IP headers, which RFC 3550 section 6.2 counts in the average RTCP packet size.
    private const int UdpIpv4Overhead = 28;
    private const int UdpIpv6Overhead = 48;

    // Room the receiver report and CNAME take in front of a feedback packet.
    private const int FeedbackPrefixCapacity = 64;

    // A per-connection canonical name, 96 random bits (RFC 7022 section 4.2), so it ties this endpoint's
    // streams together without identifying it.
    private readonly string _cname = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12));
    private readonly Lock _reportGate = new();
    private readonly Dictionary<uint, SendStatistics> _sendStatistics = [];
    private readonly Dictionary<uint, RtpReceiveStatistics> _receiveStatistics = [];
    private readonly Dictionary<uint, RemoteMember> _remoteMembers = [];
    private RtcpScheduler? _rtcpScheduler;
    private RtcpMembership _membership = new(2, 0, false);
    private long _bytesSent;
    private long _bytesReceived;
    private long _rateSampledAt = -1;
    private long _rateBytes;
    private double _mediaBitsPerSecond;
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
            _ = Interlocked.Add(ref _bytesSent, payloadLength);
            statistics.RtpTimestamp = rtpTimestamp;
            statistics.SentMicros = nowMicros;
        }
    }

    // Notes a received media packet for the reception report and the member table.
    private void RecordReceivedForReports(RtpHeader header, int length, long nowMicros)
    {
        _ = Interlocked.Add(ref _bytesReceived, length);
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
            ref RemoteMember member = ref CollectionsMarshal.GetValueRefOrAddDefault(
                _remoteMembers,
                header.Ssrc,
                out _
            );
            member = new RemoteMember(nowMicros, nowMicros);
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
    private void OnReports(ReadOnlySpan<byte> rtcp, int wireLength)
    {
        long now = NowMicros();
        NoteRtcpMembers(rtcp, wireLength, now);
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
                NoteSenderClock(ntp, now);
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

                OnStreamReported(block.Ssrc);

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
        if (_reportTimer is not null)
        {
            return;
        }

        long now = NowMicros();
        lock (_reportGate)
        {
            _rtcpScheduler = new RtcpScheduler(_options.RegularRtcpInterval);
            _membership = Membership(now);
            _rtcpScheduler.Start(now, _membership, RtcpBandwidthAt(now));
        }

        _reportTimer = _time.CreateTimer(
            static s => ((PeerConnection)s!).OnReportTimer(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
        Reschedule(now);
    }

    // RFC 3550 section 6.3.6: on expiry, time out silent members, reconsider, and send when due.
    private void OnReportTimer()
    {
        long now = NowMicros();
        bool send;
        lock (_reportGate)
        {
            if (_rtcpScheduler is not { } scheduler)
            {
                return;
            }

            RtcpBandwidth bandwidth = RtcpBandwidthAt(now);
            TimeOutMembers(now, scheduler, bandwidth);
            _membership = Membership(now);
            send = scheduler.Expire(now, _membership, bandwidth);
            if (send)
            {
                scheduler.Sent(now, _membership, bandwidth);
            }
        }

        if (send)
        {
            SendReport(goodbye: false);
        }

        Reschedule(now);
    }

    private void Reschedule(long now)
    {
        long next;
        lock (_reportGate)
        {
            next = _rtcpScheduler?.Next ?? -1;
        }

        if (next >= 0)
        {
            _ = _reportTimer?.Change(
                TimeSpan.FromMicroseconds(Math.Max(0, next - now)),
                Timeout.InfiniteTimeSpan
            );
        }
    }

    // The members as RFC 3550 section 6.3 counts them: every local reporting source and every remote
    // source heard; senders are those that sent RTP within the sender timeout (section 6.3.8).
    private RtcpMembership Membership(long now)
    {
        long senderTimeout =
            _rtcpScheduler?.SenderTimeout(_membership, RtcpBandwidthAt(now)) ?? long.MaxValue;
        int local = _sendStatistics.ContainsKey(_rtcpSenderSsrc) ? 0 : 1;
        int localSenders = 0;
        foreach (SendStatistics statistics in _sendStatistics.Values)
        {
            local++;
            if (now - statistics.SentMicros <= senderTimeout)
            {
                localSenders++;
            }
        }

        int remoteSenders = 0;
        foreach (RemoteMember member in _remoteMembers.Values)
        {
            if (member.LastRtp >= 0 && now - member.LastRtp <= senderTimeout)
            {
                remoteSenders++;
            }
        }

        return new RtcpMembership(
            local + _remoteMembers.Count,
            localSenders + remoteSenders,
            localSenders > 0
        );
    }

    // RFC 3550 section 6.3.5: a remote source silent for the member timeout leaves, and section 6.3.4's
    // reverse reconsideration follows.
    private void TimeOutMembers(long now, RtcpScheduler scheduler, RtcpBandwidth bandwidth)
    {
        long timeout = scheduler.MemberTimeout(_membership, bandwidth);
        int before = _membership.Members;
        int removed = 0;
        foreach ((uint ssrc, RemoteMember member) in _remoteMembers)
        {
            if (now - member.LastActivity > timeout)
            {
                _ = _remoteMembers.Remove(ssrc);
                _ = _receiveStatistics.Remove(ssrc);
                LogMemberTimedOut(_logger, ssrc);
                removed++;
            }
        }

        if (removed > 0)
        {
            scheduler.MembersLeft(now, before - removed, before);
        }
    }

    // A received compound packet: its size into the average (RFC 8108 section 5.3.1 divides it by the
    // sources reporting in it), its senders into the member table, and a BYE's sources out of it.
    private void NoteRtcpMembers(ReadOnlySpan<byte> rtcp, int wireLength, long now)
    {
        int reporting = 0;
        uint colliding = 0;
        lock (_reportGate)
        {
            int before = _remoteMembers.Count;
            foreach (RtcpElement element in RtcpCompound.Enumerate(rtcp))
            {
                if (element.Body.Length < 4)
                {
                    continue;
                }

                uint sender = BinaryPrimitives.ReadUInt32BigEndian(element.Body);
                if (element.PacketType == RtcpPacketType.Goodbye)
                {
                    for (
                        int i = 0;
                        i < element.ReportCount && (i + 1) * 4 <= element.Body.Length;
                        i++
                    )
                    {
                        uint leaving = BinaryPrimitives.ReadUInt32BigEndian(
                            element.Body[(i * 4)..]
                        );
                        if (_remoteMembers.Remove(leaving))
                        {
                            _ = _receiveStatistics.Remove(leaving);
                            LogMemberLeft(_logger, leaving);
                        }
                    }

                    continue;
                }

                if (
                    element.PacketType
                    is RtcpPacketType.SenderReport
                        or RtcpPacketType.ReceiverReport
                )
                {
                    reporting++;

                    // RFC 3550 section 8.2: the peer reporting from one of this side's SSRCs.
                    if (IsLocalWireSsrc(sender))
                    {
                        colliding = sender;
                    }
                }

                if (
                    element.PacketType != RtcpPacketType.SourceDescription
                    && sender != _rtcpSenderSsrc
                    && !_sendStatistics.ContainsKey(sender)
                )
                {
                    ref RemoteMember member = ref CollectionsMarshal.GetValueRefOrAddDefault(
                        _remoteMembers,
                        sender,
                        out bool known
                    );
                    member = new RemoteMember(now, known ? member.LastRtp : -1);
                }
            }

            _rtcpScheduler?.Count(wireLength + UdpIpOverhead, reporting);
            if (_remoteMembers.Count < before && _rtcpScheduler is { } scheduler)
            {
                int members = _membership.Members - (before - _remoteMembers.Count);
                scheduler.MembersLeft(now, members, _membership.Members);
                _membership = _membership with { Members = members };
            }
        }

        if (colliding != 0)
        {
            ResolveCollision(colliding, "the peer reports from it");
        }
    }

    // A sent feedback packet counts into the average RTCP size too (RFC 4585 section 3.5.4).
    private void CountRtcpSent(int protectedLength, int reportingSources = 1)
    {
        lock (_reportGate)
        {
            _rtcpScheduler?.Count(protectedLength + UdpIpOverhead, reportingSources);
        }
    }

    private int UdpIpOverhead =>
        _iceAgent?.SelectedPath is { } path
        && path.Local.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? UdpIpv6Overhead
            : UdpIpv4Overhead;

    // RTCP's bandwidth (RFC 3556): the peer's RS and RR when it states them, else 5% of the session
    // bandwidth, which is the peer's CT, the sum of its sections' AS (RFC 8859 section 6.2 sums them under
    // BUNDLE), or the measured media rate both ways.
    private RtcpBandwidth RtcpBandwidthAt(long now)
    {
        SdpDescription? remote = _remoteDescription;
        long? senders = remote?.Bandwidth.RtcpSenders ?? SumOf(remote, static b => b.RtcpSenders);
        long? receivers =
            remote?.Bandwidth.RtcpReceivers ?? SumOf(remote, static b => b.RtcpReceivers);
        long session =
            remote?.Bandwidth.ConferenceTotal
            ?? SumOf(remote, static b => b.ApplicationSpecific)
            ?? MeasuredBitsPerSecond(now);
        return RtcpBandwidth.From(
            Math.Max(session, MinimumSessionBitsPerSecond),
            senders,
            receivers
        );
    }

    // A bandwidth every section states, summed; null when one does not.
    private static long? SumOf(SdpDescription? description, Func<SdpBandwidth, long?> select)
    {
        if (description is null || description.Media.Count == 0)
        {
            return null;
        }

        long sum = 0;
        foreach (SdpMediaDescription media in description.Media)
        {
            if (media.Rejected)
            {
                continue;
            }

            if (select(media.Bandwidth) is not { } value)
            {
                return null;
            }

            sum = sum > long.MaxValue - value ? long.MaxValue : sum + value;
        }

        return sum;
    }

    // The media rate both ways, averaged over about a second.
    private long MeasuredBitsPerSecond(long now)
    {
        long bytes = Interlocked.Read(ref _bytesSent) + Interlocked.Read(ref _bytesReceived);
        if (_rateSampledAt < 0)
        {
            _rateSampledAt = now;
            _rateBytes = bytes;
            return 0;
        }

        long elapsed = now - _rateSampledAt;
        if (elapsed >= 100_000)
        {
            double rate = (bytes - _rateBytes) * 8e6 / elapsed;
            _mediaBitsPerSecond += (1 - Math.Exp(-elapsed / 1e6)) * (rate - _mediaBitsPerSecond);
            _rateSampledAt = now;
            _rateBytes = bytes;
        }

        return (long)_mediaBitsPerSecond;
    }

    // A compound report: sender reports and a receiver report for the receiving source, reception blocks,
    // the CNAME and MIDs, and a BYE when the connection closes.
    private void SendReport(bool goodbye)
    {
        if (_srtp is not { } srtp || _iceAgent is not { } agent)
        {
            return;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(1400);
        try
        {
            int length = WriteReports(buffer, goodbye, out int reporting);
            int protectedLength = srtp.ProtectRtcp(buffer, length);
            CountRtcpSent(protectedLength, reporting);
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

    private int WriteReports(Span<byte> destination, bool goodbye, out int reporting)
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
        reporting = senders.Count;
        bool receiverReports = !senders.Exists(s => s.Ssrc == _rtcpSenderSsrc);
        if (receiverReports)
        {
            // The receiving source reports too, as the member it is (RFC 8108 section 5.1), and its report
            // carries the reception blocks.
            offset += new RtcpReceiverReport(_rtcpSenderSsrc, blocks).Write(destination);
            reporting++;
        }

        {
            // The reception blocks ride on one report; the others report only themselves.
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
                    i == 0 && !receiverReports ? blocks : []
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

        Span<SdesSource> described = stackalloc SdesSource[count];
        for (int i = 0; i < count; i++)
        {
            described[i] = Described(sources[i]);
        }

        offset += RtcpSourceDescription.Write(destination[offset..], described, _cname);
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
        bool transportFeedback = remote.Media.Count > 0;
        foreach (SdpMediaDescription media in remote.Media)
        {
            reducedSize &= media.RtcpReducedSize;
            feedback &= media.CongestionControlFeedback;
            transportFeedback &= media.TransportWideFeedback;
        }

        _rtcpReducedSize = reducedSize;
        _ccfbNegotiated = feedback;
        _transportFeedbackNegotiated = transportFeedback;
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

    // A remote source in the member table: when it was last heard, and when it last sent RTP (-1 never).
    private readonly record struct RemoteMember(long LastActivity, long LastRtp);

    [LoggerMessage(
        EventId = 1190,
        Level = LogLevel.Debug,
        Message = "RTP source {Ssrc} left the session (BYE, RFC 3550 section 6.3.7)"
    )]
    private static partial void LogMemberLeft(ILogger logger, uint ssrc);

    [LoggerMessage(
        EventId = 1191,
        Level = LogLevel.Debug,
        Message = "RTP source {Ssrc} timed out of the session (RFC 3550 section 6.3.5)"
    )]
    private static partial void LogMemberTimedOut(ILogger logger, uint ssrc);

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
