using System.Buffers;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.WebRtc.Rtcp;
using Agash.StreamTransport.WebRtc.Srtp;

namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// Transport-wide congestion control feedback for <see cref="PeerConnection"/>, the feedback browsers send
/// where they do not offer RFC 8888: each packet leaving takes the next transport-wide sequence number in
/// a header extension, the receiver reports arrivals by that number, and the reports feed the same delivery
/// tracking and controller as RFC 8888's. It carries no ECN, so ECN stays off with it.
/// </summary>
public sealed partial class PeerConnection
{
    // Room a packet needs for the transport-wide sequence number when it has no extension block: a block
    // header, the element and its padding.
    private const int TransportSequenceRoom = 8;

    // Sent packets by transport-wide sequence number, for mapping reports onto them: one slot a number, so
    // sending allocates nothing.
    private readonly long[] _sentByTransport = new long[ushort.MaxValue + 1];
    private readonly TransportFeedbackTracker _transportFeedback = new();
    private readonly List<TransportPacketArrival> _transportScratch = [];
    private volatile bool _transportFeedbackNegotiated;
    private ushort _nextTransportSequence = SecureRandom.SequenceNumber();
    private byte _transportFeedbackCount;
    private uint _lastReceivedSsrc;

    /// <summary>
    /// Whether congestion feedback is transport-wide (the peer offered it and not RFC 8888, and the sequence
    /// header extension was agreed).
    /// </summary>
    private bool TransportFeedbackActive =>
        _transportFeedbackNegotiated
        && !_ccfbNegotiated
        && Volatile.Read(ref _extensions).TransportSequence != 0;

    // Whether either kind of congestion feedback flows.
    private bool FeedbackNegotiated => _ccfbNegotiated || TransportFeedbackActive;

    // In the pacer's send, before protection: the packet takes the next transport-wide number, and the
    // number maps to the sent packet. Returns the packet's length, which may have grown.
    private int StampTransportSequence(byte[] packet, int length, out ushort? sequence)
    {
        sequence = null;
        if (!TransportFeedbackActive)
        {
            return length;
        }

        ushort next = _nextTransportSequence;
        int stamped = Volatile
            .Read(ref _extensions)
            .InsertTransportSequence(
                packet.AsSpan(0, packet.Length - SrtpSession.MaxProtectionOverhead),
                length,
                next
            );
        if (stamped < 0)
        {
            return length;
        }

        _nextTransportSequence = (ushort)(next + 1);
        sequence = next;
        return stamped;
    }

    // Receive side: note an arrival for the next transport feedback report.
    private void RecordTransportArrival(uint ssrc, ushort sequence, long nowMicros)
    {
        _transportFeedback.OnPacket(sequence, nowMicros);
        _lastReceivedSsrc = ssrc;
    }

    // Under _ccGate: what is due goes out as transport feedback packets, as many as the report needs.
    private void SendTransportFeedback(SrtpSession srtp, Ice.IceAgent agent, long now)
    {
        _transportScratch.Clear();
        _transportFeedback.TakeReport(_transportScratch);
        ScheduleNextFeedback(now, 20 + (_transportScratch.Count * 2) + FeedbackOverheadBytes);
        int start = 0;
        while (start < _transportScratch.Count)
        {
            // Each packet starts at a received one, which sets its reference time.
            while (
                start < _transportScratch.Count && _transportScratch[start].ArrivalMicros is null
            )
            {
                start++;
            }

            if (start >= _transportScratch.Count)
            {
                break;
            }

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
                    + TransportFeedback.Build(
                        buffer.AsSpan(prefix),
                        _rtcpSenderSsrc,
                        _lastReceivedSsrc,
                        _transportFeedbackCount++,
                        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_transportScratch)[
                            start..
                        ],
                        MaxFeedbackPacketBytes,
                        out int consumed
                    );
                start += Math.Max(1, consumed);
                int protectedLength = srtp.ProtectRtcp(buffer, length);
                _ = agent.SendAsync(buffer.AsMemory(0, protectedLength));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogFeedbackFailed(_logger, ex);
                return;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    // Send side, under _feedbackGate then _ccGate: transport feedback mapped onto the sent packets, as
    // reports for the delivery tracker. False when the RTCP carried none.
    private bool CollectTransportFeedback(ReadOnlySpan<byte> rtcp)
    {
        _transportScratch.Clear();
        if (!TransportFeedback.TryParse(rtcp, _transportScratch))
        {
            return false;
        }

        foreach (TransportPacketArrival arrival in _transportScratch)
        {
            long id = _sentByTransport[arrival.Sequence];
            if (id == 0)
            {
                continue;
            }

            _packetReports.Add(
                new PacketReport(
                    id,
                    arrival.ArrivalMicros is not null,
                    arrival.ArrivalMicros is { } at ? TimeSpan.FromMicroseconds(at) : null,
                    EcnCodepoint.NotEct
                )
            );
        }

        return true;
    }
}
