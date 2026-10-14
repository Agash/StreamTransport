using System.Buffers;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

namespace Agash.StreamTransport.WebRtc.Rtp;

/// <summary>
/// Reorders the RTP packets of one video stream by sequence number (RFC 3550) and hands over a frame only
/// once every packet from its first to its marker packet is present, so NACK and RTX (RFC 4588) have time
/// to fill a loss before the decoder sees the frame. A keyframe comes out only with the parameter sets its
/// payload format says it needs, and after unrecoverable loss the buffer starts again at the next coded
/// sequence. Follows libwebrtc's <c>H26xPacketBuffer</c>, for any <see cref="RtpPayloadFormat"/>.
/// </summary>
/// <remarks>
/// The buffer keeps a pool-rented copy of each payload until its frame assembles or the slot is reused,
/// and assembles frames with the format's depacketizer, feeding it the frame's packets in order.
/// </remarks>
public sealed class RtpFrameBuffer : IDisposable
{
    private const int BufferSize = 2048; // ring size, indexed by seq % size (libwebrtc kBufferSize).
    private const int TrackedSequences = 5; // parallel continuity runs (libwebrtc kNumTrackedSequences).

    private readonly RtpPayloadFormat _format;
    private readonly IRtpDepacketizer _assembler;
    private readonly Slot[] _buffer = new Slot[BufferSize];
    private readonly long[] _lastContinuous = new long[TrackedSequences];
    private int _lastContinuousIndex;

    private long _lastUnwrapped;
    private int _lastSeq16 = -1;
    private long _highestInserted = long.MinValue;
    private long _lastEmittedEnd = long.MinValue;

    private struct Slot
    {
        public bool Present;
        public long Seq; // unwrapped (monotonic) sequence number.
        public uint Timestamp; // RTP timestamp (frame id; equal across a frame's packets).
        public bool Marker;
        public byte[]? Payload; // pool-rented copy of the RTP payload.
        public int Length;
    }

    /// <summary>An empty buffer for a stream of one payload format.</summary>
    /// <param name="format">The stream's payload format.</param>
    public RtpFrameBuffer(RtpPayloadFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        _format = format;
        _assembler = format.CreateDepacketizer();
        Array.Fill(_lastContinuous, long.MinValue);
    }

    /// <summary>A completed frame, owned by the caller, who disposes it.</summary>
    /// <param name="Frame">The encoded frame.</param>
    /// <param name="IsKeyframe">Whether a decoder can start from it.</param>
    /// <param name="Timestamp">Its RTP timestamp.</param>
    public readonly record struct AssembledFrame(
        EncodedFrameBuffer Frame,
        bool IsKeyframe,
        uint Timestamp
    );

    /// <summary>The outcome of inserting one packet: the frames it completed (usually 0 or 1; a late/RTX packet
    /// can complete several), and whether the receiver should request a keyframe (an IRAP missing its parameter
    /// sets, or a whole-frame gap that leaves an emitted delta frame referencing a frame that was never decoded).</summary>
    public readonly record struct InsertResult(List<AssembledFrame> Frames, bool KeyframeRequired);

    /// <summary>
    /// True while the buffer is stranded behind an unfilled sequence gap (packets received past a hole that
    /// NACK/RTX has not yet repaired). The receiver times this: if it persists beyond a recovery window it
    /// requests a keyframe to resync, rather than freezing until the next periodic GOP keyframe.
    /// </summary>
    public bool HasUnresolvedGap => _highestInserted > MaxContinuous();

    /// <summary>
    /// Insert one received RTP packet (payload borrowed for this call only). Returns the frames it completed
    /// and whether a keyframe is needed. Out-of-order and RTX-recovered packets are placed at their sequence
    /// slot and can complete frames that were waiting on them.
    /// </summary>
    public InsertResult Insert(
        ushort sequenceNumber,
        uint rtpTimestamp,
        bool marker,
        ReadOnlySpan<byte> payload
    )
    {
        long seq = Unwrap(sequenceNumber);
        ref Slot slot = ref _buffer[EuclideanMod(seq, BufferSize)];

        // A slot already holding a newer-or-equal frame means this packet is stale (sequence wrapped past it).
        if (slot.Present && slot.Seq == seq && TimestampAheadOrAt(slot.Timestamp, rtpTimestamp))
        {
            return new InsertResult([], false);
        }

        // Reuse the slot, returning any previous (overwritten/wrapped-past) payload to the pool.
        if (slot.Payload is { } old)
        {
            ArrayPool<byte>.Shared.Return(old);
        }

        byte[] copy = ArrayPool<byte>.Shared.Rent(payload.Length);
        payload.CopyTo(copy);
        slot.Present = true;
        slot.Seq = seq;
        slot.Timestamp = rtpTimestamp;
        slot.Marker = marker;
        slot.Payload = copy;
        slot.Length = payload.Length;

        if (seq > _highestInserted)
        {
            _highestInserted = seq;
        }

        var frames = new List<AssembledFrame>();
        bool keyframeRequired = FindFrames(seq, frames);
        return new InsertResult(frames, keyframeRequired);
    }

    // Walk forward from the inserted packet over the continuous run; on each frame-ending (marker) packet, walk
    // back to the frame start and try to assemble it. Mirrors H26xPacketBuffer::FindFrames.
    private bool FindFrames(long unwrappedSeq, List<AssembledFrame> frames)
    {
        bool keyframeRequired = false;

        ref Slot first = ref _buffer[EuclideanMod(unwrappedSeq, BufferSize)];

        // Establish continuity: the packet must follow a tracked continuous run, or begin a new coded
        // sequence, otherwise it is stranded behind a gap and nothing can be assembled yet.
        int runIndex = FindContinuousRun(unwrappedSeq);
        if (runIndex < 0)
        {
            if (
                (
                    _format.Inspect(first.Payload.AsSpan(0, first.Length))
                    & RtpPayloadTraits.SequenceStart
                ) == 0
            )
            {
                return false;
            }

            runIndex = _lastContinuousIndex;
            _lastContinuous[runIndex] = unwrappedSeq;
            _lastContinuousIndex = (_lastContinuousIndex + 1) % TrackedSequences;
        }

        for (long seq = unwrappedSeq; seq < unwrappedSeq + BufferSize; )
        {
            ref Slot packet = ref _buffer[EuclideanMod(seq, BufferSize)];
            if (!packet.Present || packet.Seq != seq)
            {
                return keyframeRequired;
            }

            _lastContinuous[runIndex] = seq;

            if (packet.Marker)
            {
                uint rtpTimestamp = packet.Timestamp;

                // Find the frame start: scan back while the previous slot is present with the same timestamp.
                for (long start = seq; start > seq - BufferSize; --start)
                {
                    ref Slot prev = ref _buffer[EuclideanMod(start - 1, BufferSize)];
                    if (!prev.Present || prev.Seq != start - 1 || prev.Timestamp != rtpTimestamp)
                    {
                        if (MaybeAssembleFrame(start, seq, frames, ref keyframeRequired))
                        {
                            break; // assembled; keep scanning forward for more frames.
                        }

                        return keyframeRequired; // not assemblable (missing params); stop.
                    }
                }
            }

            seq++;
        }

        return keyframeRequired;
    }

    // Validate that the frame [start..end] is a complete, decodable unit and, if so, assemble it. Mirrors
    // H26xPacketBuffer::MaybeAssembleFrame.
    private bool MaybeAssembleFrame(
        long start,
        long end,
        List<AssembledFrame> frames,
        ref bool keyframeRequired
    )
    {
        RtpPayloadTraits traits = RtpPayloadTraits.None;
        for (long seq = start; seq <= end; ++seq)
        {
            ref Slot p = ref _buffer[EuclideanMod(seq, BufferSize)];
            traits |= _format.Inspect(p.Payload.AsSpan(0, p.Length));
        }

        // A keyframe decodes only with the parameter sets it needs in the same frame; otherwise wait and ask
        // for a fresh keyframe that carries them.
        bool keyframe = (traits & RtpPayloadTraits.Keyframe) != 0;
        if (keyframe && (traits & _format.KeyframeRequires) != _format.KeyframeRequires)
        {
            keyframeRequired = true;
            return false;
        }

        // A delta frame that does not directly follow the previously emitted frame references a frame we never
        // decoded (a whole-frame gap): emit it but ask for a keyframe to reset the reference chain.
        if (!keyframe && _lastEmittedEnd != long.MinValue && start != _lastEmittedEnd + 1)
        {
            keyframeRequired = true;
        }

        uint timestamp = _buffer[EuclideanMod(end, BufferSize)].Timestamp;
        EncodedFrameBuffer frame = default;
        for (long seq = start; seq <= end; ++seq)
        {
            ref Slot p = ref _buffer[EuclideanMod(seq, BufferSize)];
            if (
                _assembler.TryPush(
                    p.Payload.AsSpan(0, p.Length),
                    seq == end,
                    out EncodedFrameBuffer completed
                )
            )
            {
                frame = completed;
            }
        }

        // Release the frame's slots back to the pool.
        for (long seq = start; seq <= end; ++seq)
        {
            ref Slot p = ref _buffer[EuclideanMod(seq, BufferSize)];
            if (p.Payload is { } buf)
            {
                ArrayPool<byte>.Shared.Return(buf);
            }

            p = default;
        }

        _lastEmittedEnd = end;
        frames.Add(new AssembledFrame(frame, keyframe, timestamp));
        return true;
    }

    private int FindContinuousRun(long unwrappedSeq)
    {
        for (int i = 0; i < TrackedSequences; i++)
        {
            if (_lastContinuous[i] == unwrappedSeq - 1)
            {
                return i;
            }
        }

        return -1;
    }

    private long MaxContinuous()
    {
        long max = long.MinValue;
        foreach (long c in _lastContinuous)
        {
            if (c > max)
            {
                max = c;
            }
        }

        return max;
    }

    // 16-bit RTP sequence number to a monotonic int64, tolerating wraparound (RFC 3550).
    private long Unwrap(ushort seq16)
    {
        if (_lastSeq16 < 0)
        {
            _lastSeq16 = seq16;
            _lastUnwrapped = seq16;
            return _lastUnwrapped;
        }

        short delta = (short)(seq16 - (ushort)_lastSeq16);
        _lastUnwrapped += delta;
        _lastSeq16 = seq16;
        return _lastUnwrapped;
    }

    // 32-bit RTP timestamp comparison with wraparound: true if a is at or ahead of b.
    private static bool TimestampAheadOrAt(uint a, uint b) => (uint)(a - b) < 0x8000_0000u;

    private static long EuclideanMod(long n, long div)
    {
        long m = n % div;
        return m < 0 ? m + div : m;
    }

    /// <summary>Returns all buffered packet payloads and the assembler buffer to the shared pool.</summary>
    public void Dispose()
    {
        foreach (ref Slot slot in _buffer.AsSpan())
        {
            if (slot.Payload is { } buf)
            {
                ArrayPool<byte>.Shared.Return(buf);
                slot.Payload = null;
            }
        }

        _assembler.Dispose();
    }
}
