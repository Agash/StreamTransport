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
    private int _newest = -1;

    /// <summary>Stores a copy of a sent RTP packet for possible retransmission.</summary>
    public void Store(ushort sequenceNumber, ReadOnlySpan<byte> rtpPacket)
    {
        byte[] copy = rtpPacket.ToArray();
        lock (_gate)
        {
            _entries[sequenceNumber % _entries.Length] = new Entry(sequenceNumber, copy);
            _newest = sequenceNumber;
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

    /// <summary>The largest of the packets stored last, the newest of them on a tie.</summary>
    /// <param name="window">How many of the newest sequence numbers to look at.</param>
    /// <param name="rtpPacket">The packet, when one of them is stored.</param>
    /// <returns>Whether one is.</returns>
    public bool TryGetLargestRecent(int window, out ReadOnlyMemory<byte> rtpPacket)
    {
        rtpPacket = default;
        lock (_gate)
        {
            if (_newest < 0)
            {
                return false;
            }

            for (int i = 0; i < Math.Min(window, _entries.Length); i++)
            {
                ushort sequence = (ushort)(_newest - i);
                Entry entry = _entries[sequence % _entries.Length];
                if (
                    entry.Packet is { } packet
                    && entry.SequenceNumber == sequence
                    && packet.Length > rtpPacket.Length
                )
                {
                    rtpPacket = packet;
                }
            }
        }

        return !rtpPacket.IsEmpty;
    }

    private readonly record struct Entry(ushort SequenceNumber, byte[]? Packet);
}
