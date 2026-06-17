using System.Buffers;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

namespace Agash.StreamTransport.WebRtc.Rtp;

/// <summary>
/// Sequence-aware H.265 packet buffer: a C# port of libwebrtc's modern <c>H26xPacketBuffer</c>
/// (<c>modules/video_coding/h26x_packet_buffer.cc</c>), the preferred receive-side frame-assembly path.
/// It reorders received RTP packets by sequence number (RFC 3550) and only emits a frame once <i>every</i>
/// packet from its first NAL to its marker packet is present and contiguous. A packet lost inside a frame
/// holds the whole frame in the buffer - giving NACK/RTX retransmission (RFC 4588) time to fill it in order -
/// instead of the old arrival-order depacketizer feeding a corrupt, or frame-merged (lost marker), access unit
/// to the decoder and cascading the HEVC reference chain. It resyncs on a new coded video sequence (a packet
/// carrying a VPS NAL), so a keyframe always restarts assembly after unrecoverable loss.
/// </summary>
/// <remarks>
/// Frame reassembly (FU/AP/single-NAL to Annex-B per RFC 7798) is delegated to <see cref="H265Depacketizer"/>,
/// fed the frame's packets in sequence order. The buffer owns a copy of each packet's payload (pool-rented)
/// until the frame assembles or the slot is reused.
/// </remarks>
public sealed class H265PacketBuffer : IDisposable
{
    private const int BufferSize = 2048;     // ring size, indexed by seq % size (libwebrtc kBufferSize).
    private const int TrackedSequences = 5;  // parallel continuity runs (libwebrtc kNumTrackedSequences).

    // H.265 NAL unit types (H.265 Table 7-1 / RFC 7798): parameter sets, aggregation, fragmentation, and the
    // IRAP VCL range (BLA/IDR/CRA) that marks a keyframe.
    private const int NalAp = 48;
    private const int NalFu = 49;
    private const int NalVps = 32;
    private const int NalSps = 33;
    private const int NalPps = 34;
    private const int IrapLow = 16;
    private const int IrapHigh = 23;

    private readonly Slot[] _buffer = new Slot[BufferSize];
    private readonly long[] _lastContinuous = new long[TrackedSequences];
    private int _lastContinuousIndex;
    private readonly H265Depacketizer _assembler = new();

    private long _lastUnwrapped;
    private int _lastSeq16 = -1;
    private long _highestInserted = long.MinValue;
    private long _lastEmittedEnd = long.MinValue;

    private struct Slot
    {
        public bool Present;
        public long Seq;          // unwrapped (monotonic) sequence number.
        public uint Timestamp;    // RTP timestamp (frame id; equal across a frame's packets).
        public bool Marker;
        public byte[]? Payload;   // pool-rented copy of the RTP payload.
        public int Length;
    }

    /// <summary>Creates an empty buffer.</summary>
    public H265PacketBuffer() => Array.Fill(_lastContinuous, long.MinValue);

    /// <summary>A completed frame: the assembled Annex-B access unit (pool-rented; the caller owns it and must
    /// return <see cref="AccessUnit"/> to <see cref="ArrayPool{T}.Shared"/>), its kind, and its RTP timestamp.</summary>
    public readonly record struct AssembledFrame(byte[] AccessUnit, int Length, bool IsKeyframe, uint Timestamp);

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
    public InsertResult Insert(ushort sequenceNumber, uint rtpTimestamp, bool marker, ReadOnlySpan<byte> payload)
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

        // Establish continuity: the packet must follow a tracked continuous run, or begin a new coded video
        // sequence (carry a VPS), otherwise it is stranded behind a gap and nothing can be assembled yet.
        int runIndex = FindContinuousRun(unwrappedSeq);
        if (runIndex < 0)
        {
            if (!BeginningOfStream(first.Payload!, first.Length))
            {
                return false;
            }

            runIndex = _lastContinuousIndex;
            _lastContinuous[runIndex] = unwrappedSeq;
            _lastContinuousIndex = (_lastContinuousIndex + 1) % TrackedSequences;
        }

        for (long seq = unwrappedSeq; seq < unwrappedSeq + BufferSize;)
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
    // H26xPacketBuffer::MaybeAssembleFrame (H.265 path).
    private bool MaybeAssembleFrame(long start, long end, List<AssembledFrame> frames, ref bool keyframeRequired)
    {
        bool hasVps = false, hasSps = false, hasPps = false, hasIrap = false;
        for (long seq = start; seq <= end; ++seq)
        {
            ref Slot p = ref _buffer[EuclideanMod(seq, BufferSize)];
            ScanNalTypes(p.Payload!, p.Length, ref hasVps, ref hasSps, ref hasPps, ref hasIrap);
        }

        // An IRAP (keyframe) is only decodable with its parameter sets in the same access unit; otherwise wait
        // (and ask for a fresh keyframe that carries them).
        if (hasIrap && (!hasVps || !hasSps || !hasPps))
        {
            keyframeRequired = true;
            return false;
        }

        // A delta frame that does not directly follow the previously emitted frame references a frame we never
        // decoded (a whole-frame gap) - emit it but ask for a keyframe to reset the reference chain.
        if (!hasIrap && _lastEmittedEnd != long.MinValue && start != _lastEmittedEnd + 1)
        {
            keyframeRequired = true;
        }

        // Reassemble Annex-B (RFC 7798) by feeding the frame's packets to the depacketizer in sequence order.
        uint timestamp = _buffer[EuclideanMod(end, BufferSize)].Timestamp;
        byte[] accessUnit = [];
        int length = 0;
        for (long seq = start; seq <= end; ++seq)
        {
            ref Slot p = ref _buffer[EuclideanMod(seq, BufferSize)];
            if (_assembler.Push(p.Payload.AsSpan(0, p.Length), p.Marker, out byte[] au, out int len))
            {
                accessUnit = au;
                length = len;
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
        frames.Add(new AssembledFrame(accessUnit, length, hasIrap, timestamp));
        return true;
    }

    // True if the packet starts a new coded video sequence (carries a VPS) - the resync point after loss.
    private static bool BeginningOfStream(byte[] payload, int length)
    {
        bool vps = false, sps = false, pps = false, irap = false;
        ScanNalTypes(payload, length, ref vps, ref sps, ref pps, ref irap);
        return vps;
    }

    // Inspect one RTP payload (single NAL, Aggregation Packet, or Fragmentation Unit) for the NAL types it
    // carries (RFC 7798 §4.4). For an FU, only the start fragment carries the original NAL type.
    private static void ScanNalTypes(byte[] payload, int length, ref bool hasVps, ref bool hasSps, ref bool hasPps, ref bool hasIrap)
    {
        if (length < 2)
        {
            return;
        }

        int type = (payload[0] >> 1) & 0x3F;
        switch (type)
        {
            case NalFu:
                if (length >= 3 && (payload[2] & 0x80) != 0) // start fragment carries the original type.
                {
                    Classify(payload[2] & 0x3F, ref hasVps, ref hasSps, ref hasPps, ref hasIrap);
                }

                break;

            case NalAp:
                // Aggregation Packet: 2-byte PayloadHdr, then [2-byte size][NAL] records.
                int offset = 2;
                while (offset + 2 <= length)
                {
                    int size = (payload[offset] << 8) | payload[offset + 1];
                    offset += 2;
                    if (size <= 0 || offset + size > length)
                    {
                        break;
                    }

                    Classify((payload[offset] >> 1) & 0x3F, ref hasVps, ref hasSps, ref hasPps, ref hasIrap);
                    offset += size;
                }

                break;

            default:
                Classify(type, ref hasVps, ref hasSps, ref hasPps, ref hasIrap);
                break;
        }
    }

    private static void Classify(int nalType, ref bool hasVps, ref bool hasSps, ref bool hasPps, ref bool hasIrap)
    {
        switch (nalType)
        {
            case NalVps: hasVps = true; break;
            case NalSps: hasSps = true; break;
            case NalPps: hasPps = true; break;
            default:
                if (nalType is >= IrapLow and <= IrapHigh) { hasIrap = true; }
                break;
        }
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
