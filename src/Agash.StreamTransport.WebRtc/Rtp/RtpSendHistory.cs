namespace Agash.StreamTransport.WebRtc.Rtp;

/// <summary>
/// A bounded ring buffer of recently sent RTP packets, keyed by sequence number, so a NACK can be served
/// by retransmission (RFC 4585/4588). Fixed capacity to bound memory on an SBC; the oldest packets fall
/// out as new ones are stored. One instance per sent SSRC.
/// </summary>
public sealed class RtpSendHistory(int capacity = 512)
{
    private readonly Entry[] _entries = new Entry[capacity];
    private readonly Lock _gate = new();

    /// <summary>Stores a copy of a sent RTP packet for possible retransmission.</summary>
    public void Store(ushort sequenceNumber, ReadOnlySpan<byte> rtpPacket)
    {
        byte[] copy = rtpPacket.ToArray();
        lock (_gate)
        {
            _entries[sequenceNumber % _entries.Length] = new Entry(sequenceNumber, copy);
        }
    }

    /// <summary>
    /// Retrieves a stored packet by sequence number if it is still present (not evicted by a later packet
    /// occupying the same slot).
    /// </summary>
    public bool TryGet(ushort sequenceNumber, out ReadOnlyMemory<byte> rtpPacket)
    {
        lock (_gate)
        {
            Entry entry = _entries[sequenceNumber % _entries.Length];
            if (entry.Packet is not null && entry.SequenceNumber == sequenceNumber)
            {
                rtpPacket = entry.Packet;
                return true;
            }
        }

        rtpPacket = default;
        return false;
    }

    private readonly record struct Entry(ushort SequenceNumber, byte[]? Packet);
}
