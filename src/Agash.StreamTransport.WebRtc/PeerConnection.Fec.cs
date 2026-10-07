using System.Buffers;
using System.Buffers.Binary;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Sdp;
using Agash.StreamTransport.WebRtc.Srtp;

namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// FlexFEC (RFC 8627) wiring for <see cref="PeerConnection"/>, as negotiated: the send side emits a repair
/// packet per group of its protected video packets on its FEC SSRC when the peer accepted the
/// <c>flexfec</c> codec; the receive side caches the peer's protected media and recovers a single lost
/// packet per group from the peer's repair, delivering it through the normal receive path.
/// </summary>
public sealed partial class PeerConnection
{
    private readonly FlexFecAccumulator _fecAccumulator = new();
    private readonly Lock _fecGate = new();

    // The peer's last 256 protected packets for recovery, a slot per sequence number modulo 256, each
    // slot's buffer reused: no allocation per packet.
    private const int ReceiveSlots = 256;
    private const int SlotBytes = 1500;
    private readonly byte[][] _fecRecvBodies = new byte[ReceiveSlots][];
    private readonly FecSourcePacket?[] _fecRecvPackets = new FecSourcePacket?[ReceiveSlots];
    private FecConfiguration _fec = FecConfiguration.None;

    private static readonly TimeSpan ReplanInterval = TimeSpan.FromMilliseconds(500);

    // The FEC group size the recovery policy set: 0 sends no repairs. Off until loss is seen.
    private int _fecGroupSize;
    private RecoveryPolicy? _recovery;
    private long _protectedPackets;
    private long _protectedFrames;
    private TimeSpan _replannedAt;

    private bool FecEnabled => Volatile.Read(ref _fec).Send is not null;

    // What FlexFEC the negotiation agreed: this endpoint protects its media on its FEC SSRC when both
    // sides list the flexfec codec and it announced an FEC SSRC; it recovers the peer's media from the
    // repairs on the SSRC the peer's FEC-FR group names. RFC 8627 section 1.1.7 has an answer to an offer
    // of both FlexFEC and RTX keep only FlexFEC; this endpoint keeps both, as libwebrtc does, using FEC
    // parity next to RFC 4588 retransmission.
    private void ConfigureFec(SdpDescription remote)
    {
        FecSender? send = null;
        FecReceiver? receive = null;
        foreach (NegotiatedMediaInfo media in NegotiatedMedia)
        {
            if (
                media.Codecs.FirstOrDefault(FlexFec.IsFlexFec) is not { EncodingName: not null } fec
            )
            {
                continue;
            }

            if (LocalFecSsrcFor(media.Mid, media.Kind) is { } local && media.LocalSsrc != 0)
            {
                send = new FecSender(media.LocalSsrc, local, (byte)fec.PayloadType);
            }

            if (
                remote.Media.FirstOrDefault(m => m.Mid == media.Mid) is
                { Ssrc: { } remoteMedia, FecSsrc: { } remoteFec }
            )
            {
                receive = new FecReceiver(remoteMedia, remoteFec);
            }
        }

        Volatile.Write(ref _fec, new FecConfiguration(send, receive));
        LogFec(send is not null, receive is not null);
        UpdateDatagramLimit();
    }

    // Send side: fold the protected media packet into the group's parity, in place; once the group is
    // complete, the repair RTP packet, written straight from the parity into a pooled buffer, for the
    // caller to queue after the media packet. Every protected packet counts toward the frame statistics
    // the policy plans from.
    private PacedPacket? AccumulateFec(ReadOnlySpan<byte> cleartextRtp, FecSender sender)
    {
        _ = Interlocked.Increment(ref _protectedPackets);
        if ((cleartextRtp[1] & 0x80) != 0)
        {
            _ = Interlocked.Increment(ref _protectedFrames);
        }

        int group = Volatile.Read(ref _fecGroupSize);
        lock (_fecGate)
        {
            if (group <= 0)
            {
                _fecAccumulator.Reset();
                return null;
            }

            _fecAccumulator.Add(cleartextRtp);
            if (
                !_fecAccumulator.TryComplete(
                    Math.Min(group, FlexFec.MaxProtected),
                    out ReadOnlySpan<byte> body
                )
            )
            {
                return null;
            }

            byte[] repair = ArrayPool<byte>.Shared.Rent(
                RtpPacket.FixedHeaderLength + body.Length + SrtpSession.MaxProtectionOverhead
            );
            int length = RtpPacket.Write(
                repair,
                false,
                sender.PayloadType,
                NextSequence(sender.FecSsrc),
                0,
                sender.FecSsrc,
                body
            );
            return new PacedPacket(repair, length, TrafficClass.Repair);
        }
    }

    // Plans the repair from the path and what was sent since the last plan; runs on the process timer.
    private void ReplanRecovery(CapacityEstimate estimate, TimeSpan now)
    {
        if (!FecEnabled || now - _replannedAt < ReplanInterval)
        {
            return;
        }

        double seconds = (now - _replannedAt).TotalSeconds;
        _replannedAt = now;
        long packets = Interlocked.Exchange(ref _protectedPackets, 0);
        long frames = Interlocked.Exchange(ref _protectedFrames, 0);
        if (frames == 0)
        {
            return;
        }

        _recovery ??= new RecoveryPolicy(_options.Recovery);
        RecoveryPlan plan = _recovery.Plan(
            new RecoveryInputs(
                _lossRate,
                estimate.SmoothedRoundTrip,
                estimate.TargetBitsPerSecond,
                frames / seconds,
                (double)packets / frames
            )
        );
        if (Interlocked.Exchange(ref _fecGroupSize, plan.FecGroupSize) != plan.FecGroupSize)
        {
            LogRecoveryPlan(plan.FecGroupSize, _lossRate, estimate.SmoothedRoundTrip);
        }
    }

    // Receive side: cache a protected media packet so a later repair can recover a neighbour.
    private void CacheProtectedPacket(ReadOnlySpan<byte> cleartextRtp)
    {
        ReadOnlySpan<byte> body = cleartextRtp[12..];
        if (body.Length > SlotBytes)
        {
            return;
        }

        ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(cleartextRtp[2..]);
        int slot = sequence % ReceiveSlots;
        lock (_fecGate)
        {
            byte[] buffer = _fecRecvBodies[slot] ??= new byte[SlotBytes];
            body.CopyTo(buffer);
            _fecRecvPackets[slot] = new FecSourcePacket(
                sequence,
                (byte)((cleartextRtp[0] & 0x3F) | (((cleartextRtp[1] >> 7) & 1) << 6)),
                (byte)(cleartextRtp[1] & 0x7F),
                BinaryPrimitives.ReadUInt32BigEndian(cleartextRtp[4..]),
                buffer.AsMemory(0, body.Length)
            );
        }
    }

    // A cached protected packet by sequence number, when its slot still holds it.
    private FecSourcePacket? Cached(ushort sequence) =>
        _fecRecvPackets[sequence % ReceiveSlots] is { } packet && packet.SequenceNumber == sequence
            ? packet
            : null;

    // Receive side: a repair arrived - recover a lost media packet and deliver it through the normal path.
    private void OnFecPacket(ReadOnlySpan<byte> fecBody, uint protectedSsrc)
    {
        FecRecoveredPacket? recovered;
        lock (_fecGate)
        {
            recovered = FlexFec.TryRecover(fecBody, Cached);
        }

        if (recovered is not { } r)
        {
            return;
        }

        // Rebuild the full RTP packet from the recovered fields + the protected SSRC, then run it through the
        // same parse/dispatch as a real arrival (so its header extension parses identically).
        byte[] rtp = new byte[12 + r.BodyAfterHeader.Length];
        rtp[0] = (byte)(0x80 | (r.HeaderBits & 0x3F));
        rtp[1] = (byte)((((r.HeaderBits >> 6) & 1) << 7) | r.PayloadType);
        BinaryPrimitives.WriteUInt16BigEndian(rtp.AsSpan(2), r.SequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(4), r.Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(8), protectedSsrc);
        r.BodyAfterHeader.CopyTo(rtp.AsSpan(12));

        if (RtpPacket.TryParse(rtp, out RtpHeader header, out ReadOnlySpan<byte> payload))
        {
            if (Volatile.Read(ref _rtx).Repairable.Contains(header.Ssrc))
            {
                OnMediaSequence(header.Ssrc, header.SequenceNumber);
            }

            CacheProtectedPacket(rtp);
            int payloadOffset = rtp.Length - payload.Length;
            RtpReceived?.Invoke(header, rtp.AsMemory(payloadOffset, payload.Length));
        }
    }
}
