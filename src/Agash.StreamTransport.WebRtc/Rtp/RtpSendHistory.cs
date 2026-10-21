namespace Agash.StreamTransport.WebRtc.Rtp;

/// <summary>
/// A bounded ring of recently sent RTP packets, keyed by sequence number, so a NACK can be served by
/// retransmission (RFC 4585/4588). Each slot keeps its own buffer, grown once to the largest packet it has
/// held, so storing a packet copies it without allocating; readers copy it out under the lock, so a slot
/// a newer packet reuses never changes under them. Fixed capacity to bound memory on an SBC. One instance
/// per sent SSRC.
/// </summary>
/// <param name="capacity">How many packets it holds.</param>
public sealed class RtpSendHistory(int capacity = 512)
{
    private readonly byte[]?[] _buffers = new byte[capacity][];
    private readonly Entry[] _entries = new Entry[capacity];
    private readonly Lock _gate = new();
    private int _newest = -1;

    /// <summary>The longest packet <see cref="TryCopy"/> returns, a full Ethernet MTU.</summary>
    public const int MaximumPacketLength = 1500;

    /// <summary>Stores a copy of a sent RTP packet for possible retransmission.</summary>
    /// <param name="sequenceNumber">Its sequence number.</param>
    /// <param name="rtpPacket">The packet.</param>
    public void Store(ushort sequenceNumber, ReadOnlySpan<byte> rtpPacket)
    {
        if (rtpPacket.Length > MaximumPacketLength)
        {
            return;
        }

        int slot = sequenceNumber % _entries.Length;
        lock (_gate)
        {
            byte[] buffer =
                _buffers[slot] is { } held && held.Length >= rtpPacket.Length
                    ? held
                    : _buffers[slot] = new byte[Math.Max(rtpPacket.Length, 256)];
            rtpPacket.CopyTo(buffer);
            _entries[slot] = new Entry(sequenceNumber, rtpPacket.Length);
            _newest = sequenceNumber;
        }
    }

    /// <summary>
    /// Copies out a stored packet by sequence number, if it is still held (not displaced by a later packet
    /// in the same slot).
    /// </summary>
    /// <param name="sequenceNumber">Its sequence number.</param>
    /// <param name="destination">Where it goes, <see cref="MaximumPacketLength"/> bytes or more.</param>
    /// <param name="length">Its length.</param>
    /// <returns>Whether it was held.</returns>
    public bool TryCopy(ushort sequenceNumber, Span<byte> destination, out int length)
    {
        lock (_gate)
        {
            Entry entry = _entries[sequenceNumber % _entries.Length];
            if (entry.Length > 0 && entry.SequenceNumber == sequenceNumber)
            {
                length = entry.Length;
                _buffers[sequenceNumber % _entries.Length].AsSpan(0, length).CopyTo(destination);
                return true;
            }
        }

        length = 0;
        return false;
    }

    /// <summary>Copies out the largest of the packets stored last, the newest of them on a tie.</summary>
    /// <param name="window">How many of the newest sequence numbers to look at.</param>
    /// <param name="destination">Where it goes, <see cref="MaximumPacketLength"/> bytes or more.</param>
    /// <param name="length">Its length.</param>
    /// <returns>Whether one is held.</returns>
    public bool TryCopyLargestRecent(int window, Span<byte> destination, out int length)
    {
        length = 0;
        lock (_gate)
        {
            if (_newest < 0)
            {
                return false;
            }

            int best = -1;
            for (int i = 0; i < Math.Min(window, _entries.Length); i++)
            {
                ushort sequence = (ushort)(_newest - i);
                int slot = sequence % _entries.Length;
                Entry entry = _entries[slot];
                if (entry.Length > length && entry.SequenceNumber == sequence)
                {
                    length = entry.Length;
                    best = slot;
                }
            }

            if (best < 0)
            {
                return false;
            }

            _buffers[best].AsSpan(0, length).CopyTo(destination);
            return true;
        }
    }

    private readonly record struct Entry(ushort SequenceNumber, int Length);
}
