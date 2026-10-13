using System.Buffers.Binary;

namespace Agash.StreamTransport.WebRtc.Rtp;

/// <summary>
/// Builds a FlexFEC repair as its source packets go out: each packet's header fields, length and the bytes
/// after its fixed header are XORed into one running parity buffer, so a group costs no copy or allocation
/// per packet. <see cref="TryComplete"/> hands out the repair body (the FEC header and the parity) once the
/// group is full, the same bytes <see cref="FlexFec.BuildRepair"/> makes from the packets themselves.
/// </summary>
/// <remarks>Not thread-safe: the sender feeds it one packet at a time.</remarks>
internal sealed class FlexFecAccumulator
{
    // Room for the largest RTP packet that fits a datagram.
    private const int MaxBody = 1500;

    // The parity sits after room for the longest header; a repair's header is written just before it.
    private readonly byte[] _repair = new byte[FlexFec.MaxHeaderLength + MaxBody];
    private int _maxBody;
    private int _span;
    private ushort _snBase;
    private UInt128 _mask;
    private byte _headerBits;
    private byte _second;
    private uint _timestamp;
    private ushort _length;

    // The parity of the last repair is cleared lazily, before the next packet folds in, so the span
    // TryComplete handed out stays valid until then.
    private int _pendingClear;

    /// <summary>How many packets the current group holds.</summary>
    public int Count { get; private set; }

    /// <summary>The newest protected packet's timestamp: the repair's send time on the media clock.</summary>
    public uint LatestTimestamp { get; private set; }

    /// <summary>Folds a cleartext RTP packet of the protected stream into the group.</summary>
    /// <param name="rtp">The packet, its fixed header first.</param>
    public void Add(ReadOnlySpan<byte> rtp)
    {
        ReadOnlySpan<byte> body = rtp[RtpPacket.FixedHeaderLength..];
        ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(rtp[2..]);
        if (Count == 0)
        {
            _snBase = sequence;
        }

        int offset = (ushort)(sequence - _snBase);
        if (offset >= FlexFec.MaxProtected || body.Length > MaxBody)
        {
            // Outside what one mask covers: start the group again from this packet.
            Reset();
            _snBase = sequence;
            offset = 0;
        }

        uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(rtp[4..]);
        _mask |= FlexFec.MaskBit(offset);
        _span = Math.Max(_span, offset + 1);
        _headerBits ^= (byte)(rtp[0] & 0x3F);
        _second ^= rtp[1];
        _timestamp ^= timestamp;
        _length ^= (ushort)body.Length;
        _maxBody = Math.Max(_maxBody, body.Length);
        LatestTimestamp = timestamp;
        Clear();
        Parity.Xor(_repair.AsSpan(FlexFec.MaxHeaderLength, body.Length), body);
        Count++;
    }

    /// <summary>The repair body once the group holds <paramref name="groupSize"/> packets.</summary>
    /// <param name="groupSize">The packets one repair protects.</param>
    /// <param name="repair">The FEC header and parity, valid until the next <see cref="Add"/>.</param>
    /// <returns>Whether the group was complete; it starts again empty when it was.</returns>
    public bool TryComplete(int groupSize, out ReadOnlySpan<byte> repair)
    {
        if (Count < groupSize)
        {
            repair = default;
            return false;
        }

        int header = FlexFec.HeaderLength(_span);
        FlexFec.WriteHeader(
            _repair.AsSpan(FlexFec.MaxHeaderLength - header, header),
            _headerBits,
            _second,
            _length,
            _timestamp,
            _snBase,
            _mask
        );
        repair = _repair.AsSpan(FlexFec.MaxHeaderLength - header, header + _maxBody);
        _pendingClear = _maxBody;
        ResetFields();
        return true;
    }

    /// <summary>Drops the group.</summary>
    public void Reset()
    {
        _pendingClear = Math.Max(_pendingClear, _maxBody);
        ResetFields();
        Clear();
    }

    private void ResetFields()
    {
        Count = 0;
        _maxBody = 0;
        _span = 0;
        _mask = 0;
        _headerBits = 0;
        _second = 0;
        _timestamp = 0;
        _length = 0;
    }

    private void Clear()
    {
        if (_pendingClear > 0)
        {
            _repair.AsSpan(FlexFec.MaxHeaderLength, _pendingClear).Clear();
            _pendingClear = 0;
        }
    }
}
