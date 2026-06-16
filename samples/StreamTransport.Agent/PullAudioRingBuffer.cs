namespace StreamTransport.Agent;

/// <summary>
/// A thread-safe PCM ring buffer for pull-model audio outputs. Decoded audio is <see cref="Write"/>-pushed by
/// the decode thread; the OS audio device <see cref="Read"/>-pulls on its own callback thread, draining across
/// chunk boundaries. The backlog is bounded: on overflow the oldest whole chunks are dropped, keeping playout
/// close to live (latency bounded) rather than growing without limit when the consumer falls behind. An
/// underrun (nothing queued) returns a short read; the caller fills the remainder with silence.
///
/// <para>Shared by every platform audio sink - PipeWire (<c>FillSamples</c>), NAudio (<c>IWaveProvider.Read</c>)
/// and macOS AudioUnit (render callback) are all pull consumers, so the queue/offset/bounded-drop/drain logic
/// lives here once instead of being re-implemented per sink.</para>
/// </summary>
internal sealed class PullAudioRingBuffer(int maxBytes)
{
    private readonly Lock _gate = new();
    private readonly Queue<byte[]> _chunks = new();
    private int _headOffset;   // bytes already consumed from the chunk at the head of the queue
    private int _queuedBytes;  // total bytes across queued chunks (including the consumed head prefix)

    /// <summary>Queue a copy of <paramref name="samples"/>, dropping the oldest chunks if over the byte cap.</summary>
    public void Write(ReadOnlySpan<byte> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        byte[] chunk = samples.ToArray();
        lock (_gate)
        {
            _chunks.Enqueue(chunk);
            _queuedBytes += chunk.Length;

            // Overflow: drop the oldest whole chunks (and reset any partial head) until back under the cap.
            while (_queuedBytes - _headOffset > maxBytes && _chunks.Count > 1)
            {
                byte[] dropped = _chunks.Dequeue();
                _queuedBytes -= dropped.Length;
                _headOffset = 0;
            }
        }
    }

    /// <summary>Drain up to <paramref name="dst"/>.Length bytes into it; returns how many were written (0..len).</summary>
    public int Read(Span<byte> dst)
    {
        lock (_gate)
        {
            int written = 0;
            while (written < dst.Length && _chunks.Count > 0)
            {
                byte[] head = _chunks.Peek();
                int take = Math.Min(head.Length - _headOffset, dst.Length - written);
                head.AsSpan(_headOffset, take).CopyTo(dst.Slice(written, take));
                written += take;
                _headOffset += take;
                _queuedBytes -= take;
                if (_headOffset >= head.Length)
                {
                    _chunks.Dequeue();
                    _headOffset = 0;
                }
            }

            return written;
        }
    }

    /// <summary>Drop all queued audio.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _chunks.Clear();
            _headOffset = 0;
            _queuedBytes = 0;
        }
    }
}
