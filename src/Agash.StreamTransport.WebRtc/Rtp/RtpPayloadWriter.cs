namespace Agash.StreamTransport.WebRtc.Rtp;

/// <summary>
/// Reusable, growable storage for the RTP payloads of a single frame, laid out back-to-back so a packetizer
/// emits them with no per-payload allocation. One instance per send stream: call <see cref="Reset"/> before a
/// frame, <see cref="Add"/> one writable span per payload, then read them back via <see cref="Count"/> and the
/// indexer. The produced payloads stay valid until the next <see cref="Reset"/>. Not thread-safe.
/// </summary>
public sealed class RtpPayloadWriter
{
    private byte[] _buffer;
    private (int Offset, int Length)[] _segments = new (int, int)[32];
    private int _used;

    /// <summary>Creates a writer with an initial backing buffer of <paramref name="initialCapacity"/> bytes.</summary>
    /// <param name="initialCapacity">The starting buffer size; it grows on demand and is reused across frames.</param>
    public RtpPayloadWriter(int initialCapacity = 64 * 1024) => _buffer = new byte[initialCapacity];

    /// <summary>The number of payloads produced since the last <see cref="Reset"/>.</summary>
    public int Count { get; private set; }

    /// <summary>Drops the previous frame's payloads, keeping the backing storage for reuse.</summary>
    public void Reset()
    {
        Count = 0;
        _used = 0;
    }

    /// <summary>
    /// Reserves the next payload of <paramref name="length"/> bytes and returns its writable span. The caller
    /// fills the span before the next <see cref="Add"/>; growth here does not invalidate already-filled
    /// payloads (their bytes are preserved) but earlier returned spans must not be retained across this call.
    /// </summary>
    /// <param name="length">The payload length in bytes.</param>
    /// <returns>A writable span of exactly <paramref name="length"/> bytes for the new payload.</returns>
    public Span<byte> Add(int length)
    {
        if (_used + length > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _used + length));
        }

        if (Count == _segments.Length)
        {
            Array.Resize(ref _segments, _segments.Length * 2);
        }

        _segments[Count++] = (_used, length);
        Span<byte> span = _buffer.AsSpan(_used, length);
        _used += length;
        return span;
    }

    /// <summary>The <paramref name="index"/>-th payload produced since the last <see cref="Reset"/>.</summary>
    /// <param name="index">A payload index in <c>[0, Count)</c>.</param>
    /// <returns>A view of the payload bytes, valid until the next <see cref="Reset"/>.</returns>
    public ReadOnlyMemory<byte> this[int index]
    {
        get
        {
            (int offset, int length) = _segments[index];
            return _buffer.AsMemory(offset, length);
        }
    }
}
