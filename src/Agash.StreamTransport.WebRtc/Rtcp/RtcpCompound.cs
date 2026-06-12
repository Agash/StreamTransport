namespace Agash.StreamTransport.WebRtc.Rtcp;

/// <summary>One packet within an RTCP compound packet (RFC 3550 §6.1), borrowed from the source buffer.</summary>
public readonly ref struct RtcpElement(RtcpPacketType packetType, int reportCount, ReadOnlySpan<byte> body)
{
    /// <summary>The packet type.</summary>
    public RtcpPacketType PacketType { get; } = packetType;

    /// <summary>The report count / feedback-message-type field (the low 5 bits of the first octet).</summary>
    public int ReportCount { get; } = reportCount;

    /// <summary>The packet body after the 4-octet RTCP header.</summary>
    public ReadOnlySpan<byte> Body { get; } = body;
}

/// <summary>
/// Walks the individual packets of an RTCP compound packet (RFC 3550 §6.1) without allocating, validating
/// the per-packet length fields against the buffer. Used as <c>foreach (var e in RtcpCompound.Enumerate(buf))</c>.
/// </summary>
public readonly ref struct RtcpCompound
{
    private readonly ReadOnlySpan<byte> _packet;

    private RtcpCompound(ReadOnlySpan<byte> packet) => _packet = packet;

    /// <summary>Begins enumeration over <paramref name="packet"/>.</summary>
    public static RtcpCompound Enumerate(ReadOnlySpan<byte> packet) => new(packet);

    /// <summary>Returns the enumerator (the foreach pattern).</summary>
    public Enumerator GetEnumerator() => new(_packet);

    /// <summary>The ref-struct enumerator over the compound packet's elements.</summary>
    public ref struct Enumerator(ReadOnlySpan<byte> packet)
    {
        private readonly ReadOnlySpan<byte> _packet = packet;
        private int _offset;

        /// <summary>The current element.</summary>
        public RtcpElement Current { get; private set; }

        /// <summary>Advances to the next valid RTCP packet in the compound.</summary>
        public bool MoveNext()
        {
            if (_offset + 4 > _packet.Length)
            {
                return false;
            }

            int reportCount = _packet[_offset] & 0x1F;
            var type = (RtcpPacketType)_packet[_offset + 1];
            int lengthWords = (_packet[_offset + 2] << 8) | _packet[_offset + 3];
            int bodyStart = _offset + 4;
            int bodyEnd = bodyStart + (lengthWords * 4);
            if ((_packet[_offset] >> 6) != 2 || bodyEnd > _packet.Length)
            {
                return false;
            }

            Current = new RtcpElement(type, reportCount, _packet[bodyStart..bodyEnd]);
            _offset = bodyEnd;
            return true;
        }
    }
}
