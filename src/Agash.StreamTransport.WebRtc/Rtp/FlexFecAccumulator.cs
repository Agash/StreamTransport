using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

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
    // Room for the FEC header and the largest RTP packet that fits a datagram.
    private const int MaxBody = 1500;

    private readonly byte[] _repair = new byte[FlexFec.HeaderLength + MaxBody];
    private int _maxBody;
    private ushort _snBase;
    private ushort _mask;
    private byte _headerBits;
    private byte _payloadType;
    private uint _timestamp;
    private ushort _length;

    /// <summary>How many packets the current group holds.</summary>
    public int Count { get; private set; }

    /// <summary>Folds a cleartext RTP packet of the protected stream into the group.</summary>
    /// <param name="rtp">The packet, its fixed header first.</param>
    public void Add(ReadOnlySpan<byte> rtp)
    {
        ReadOnlySpan<byte> body = rtp[12..];
        ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(rtp[2..]);
        if (Count == 0)
        {
            _snBase = sequence;
        }

        int offset = (ushort)(sequence - _snBase);
        if (offset >= FlexFec.MaxProtected || body.Length > MaxBody)
        {
            // Outside what one 15-bit mask covers: start the group again from this packet.
            Reset();
            _snBase = sequence;
            offset = 0;
        }

        _mask |= (ushort)(1 << (14 - offset));
        _headerBits ^= (byte)((rtp[0] & 0x3F) | (((rtp[1] >> 7) & 1) << 6));
        _payloadType ^= (byte)(rtp[1] & 0x7F);
        _timestamp ^= BinaryPrimitives.ReadUInt32BigEndian(rtp[4..]);
        _length ^= (ushort)body.Length;
        _maxBody = Math.Max(_maxBody, body.Length);
        Xor(_repair.AsSpan(FlexFec.HeaderLength, body.Length), body);
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

        Span<byte> header = _repair.AsSpan(0, FlexFec.HeaderLength);
        header[0] = (byte)(_headerBits & 0x3F);
        header[1] = (byte)(_payloadType & 0x7F);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], _length);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], _timestamp);
        BinaryPrimitives.WriteUInt16BigEndian(header[8..], _snBase);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..], (ushort)(_mask & 0x7FFF));
        repair = _repair.AsSpan(0, FlexFec.HeaderLength + _maxBody);
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

    // The parity of the last repair is cleared lazily, before the next packet folds in, so the span
    // TryComplete handed out stays valid until then.
    private int _pendingClear;

    private void ResetFields()
    {
        Count = 0;
        _maxBody = 0;
        _mask = 0;
        _headerBits = 0;
        _payloadType = 0;
        _timestamp = 0;
        _length = 0;
    }

    private void Clear()
    {
        if (_pendingClear > 0)
        {
            _repair.AsSpan(FlexFec.HeaderLength, _pendingClear).Clear();
            _pendingClear = 0;
        }
    }

    private void Xor(Span<byte> parity, ReadOnlySpan<byte> body)
    {
        Clear();
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            Span<Vector<byte>> wide = MemoryMarshal.Cast<byte, Vector<byte>>(parity);
            ReadOnlySpan<Vector<byte>> from = MemoryMarshal.Cast<byte, Vector<byte>>(body);
            for (int v = 0; v < wide.Length; v++)
            {
                wide[v] ^= from[v];
            }

            i = wide.Length * Vector<byte>.Count;
        }

        for (; i < body.Length; i++)
        {
            parity[i] ^= body[i];
        }
    }
}
