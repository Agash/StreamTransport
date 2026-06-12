using System.Buffers.Binary;
using Agash.StreamTransport.WebRtc.Rtp;

namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// FlexFEC (RFC 8627) wiring for <see cref="PeerConnection"/>, gated by <see cref="PeerConnectionOptions.EnableFec"/>:
/// the send side emits a repair packet per group of protected video packets on a dedicated SSRC; the receive
/// side caches protected media and recovers a single lost packet per group from a repair, delivering it through
/// the normal receive path. Loss repair with no retransmit round trip - the IRL profile's loss-masking.
/// </summary>
public sealed partial class PeerConnection
{
    private readonly List<FecSourcePacket> _fecSendWindow = [];
    private readonly Lock _fecGate = new();
    private readonly Dictionary<ushort, FecSourcePacket> _fecRecvCache = [];
    private readonly Queue<ushort> _fecRecvOrder = [];

    private bool FecEnabled => _options.EnableFec && _options.FecProtectedSsrc != 0;

    private static FecSourcePacket ToFecSource(ReadOnlySpan<byte> cleartextRtp)
    {
        byte headerBits = (byte)((cleartextRtp[0] & 0x3F) | (((cleartextRtp[1] >> 7) & 1) << 6)); // P/X/CC + M.
        byte pt = (byte)(cleartextRtp[1] & 0x7F);
        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(cleartextRtp[2..]);
        uint ts = BinaryPrimitives.ReadUInt32BigEndian(cleartextRtp[4..]);
        return new FecSourcePacket(seq, headerBits, pt, ts, cleartextRtp[12..].ToArray());
    }

    // Send side: accumulate the protected media packet; return a repair body to send once a group is complete
    // (the caller sends it after the media packet, on the same chain, so SRTP protect is never concurrent).
    private byte[]? AccumulateFec(ReadOnlySpan<byte> cleartextRtp)
    {
        lock (_fecGate)
        {
            _fecSendWindow.Add(ToFecSource(cleartextRtp));
            if (_fecSendWindow.Count < Math.Clamp(_options.FecGroupSize, 1, FlexFec.MaxProtected))
            {
                return null;
            }

            byte[] repair = FlexFec.BuildRepair(_fecSendWindow);
            _fecSendWindow.Clear();
            return repair;
        }
    }

    // Receive side: cache a protected media packet so a later repair can recover a neighbour.
    private void CacheProtectedPacket(ReadOnlySpan<byte> cleartextRtp)
    {
        FecSourcePacket source = ToFecSource(cleartextRtp);
        lock (_fecGate)
        {
            if (_fecRecvCache.TryAdd(source.SequenceNumber, source))
            {
                _fecRecvOrder.Enqueue(source.SequenceNumber);
                while (_fecRecvOrder.Count > 256)
                {
                    _fecRecvCache.Remove(_fecRecvOrder.Dequeue());
                }
            }
        }
    }

    // Receive side: a repair arrived - recover a lost media packet and deliver it through the normal path.
    private void OnFecPacket(ReadOnlySpan<byte> fecBody)
    {
        FecRecoveredPacket? recovered;
        lock (_fecGate)
        {
            recovered = FlexFec.TryRecover(fecBody, seq => _fecRecvCache.TryGetValue(seq, out FecSourcePacket s) ? s : null);
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
        BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(8), _options.FecProtectedSsrc);
        r.BodyAfterHeader.CopyTo(rtp.AsSpan(12));

        if (RtpPacket.TryParse(rtp, out RtpHeader header, out ReadOnlySpan<byte> payload))
        {
            CacheProtectedPacket(rtp);
            int payloadOffset = rtp.Length - payload.Length;
            RtpReceived?.Invoke(header, rtp.AsMemory(payloadOffset, payload.Length));
        }
    }
}
