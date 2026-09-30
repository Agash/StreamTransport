using System.Buffers;

namespace Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

/// <summary>
/// Packetizes the Annex-B access units of a NAL-unit codec (H.264, H.265 and their relatives) into RTP
/// payloads: consecutive NAL units that fit together share an aggregation packet, a NAL unit that fits
/// alone is a single-NAL-unit packet, and a larger one is split into fragmentation units. A codec supplies
/// its aggregation and fragmentation header layouts.
/// </summary>
public abstract class NalUnitPacketizer : IRtpPacketizer
{
    private const int LengthSize = 2;

    private readonly int _maxPayloadSize;
    private readonly int _nalHeaderSize;
    private readonly int _fragmentHeaderSize;

    /// <summary>Sets the payload limit and the codec's header sizes.</summary>
    /// <param name="maxPayloadSize">The largest RTP payload in bytes.</param>
    /// <param name="nalHeaderSize">The size of the codec's NAL unit header, which an aggregation header matches.</param>
    /// <param name="fragmentHeaderSize">The size of the headers that open each fragmentation unit.</param>
    protected NalUnitPacketizer(int maxPayloadSize, int nalHeaderSize, int fragmentHeaderSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nalHeaderSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(fragmentHeaderSize, nalHeaderSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadSize, fragmentHeaderSize + 1);
        _maxPayloadSize = maxPayloadSize;
        _nalHeaderSize = nalHeaderSize;
        _fragmentHeaderSize = fragmentHeaderSize;
    }

    /// <summary>Splits an Annex-B access unit into RTP payloads.</summary>
    /// <param name="frame">The access unit, NAL units separated by Annex-B start codes.</param>
    /// <param name="writer">Reusable payload storage; reset before writing.</param>
    public void Packetize(ReadOnlySpan<byte> frame, RtpPayloadWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Reset();

        // Pending NAL units are consecutive in the access unit, so a run is a start, an end and a count.
        int runStart = 0;
        int runEnd = 0;
        int runCount = 0;
        int runPayload = 0;
        foreach (Range range in new AnnexBNalUnits(frame))
        {
            ReadOnlySpan<byte> nal = frame[range];
            if (nal.Length < _nalHeaderSize)
            {
                continue;
            }

            if (
                runCount > 0
                && _nalHeaderSize + runPayload + LengthSize + nal.Length <= _maxPayloadSize
            )
            {
                runEnd = range.End.Value;
                runCount++;
                runPayload += LengthSize + nal.Length;
                continue;
            }

            Flush(frame[runStart..runEnd], runCount, runPayload, writer);
            runCount = 0;
            if (nal.Length <= _maxPayloadSize)
            {
                (runStart, runEnd, runCount) = (range.Start.Value, range.End.Value, 1);
                runPayload = LengthSize + nal.Length;
            }
            else
            {
                Fragment(nal, writer);
            }
        }

        Flush(frame[runStart..runEnd], runCount, runPayload, writer);
    }

    /// <summary>Writes the header of an aggregation packet carrying a run of NAL units.</summary>
    /// <param name="header">The header, <c>nalHeaderSize</c> bytes.</param>
    /// <param name="run">The run in Annex-B form, opening with the first unit's header and no start code.</param>
    protected abstract void WriteAggregationHeader(Span<byte> header, ReadOnlySpan<byte> run);

    /// <summary>Writes the headers that open one fragmentation unit of a NAL unit.</summary>
    /// <param name="header">The headers, <c>fragmentHeaderSize</c> bytes.</param>
    /// <param name="nalHeader">The fragmented NAL unit's header.</param>
    /// <param name="first">Whether this is the first fragment.</param>
    /// <param name="last">Whether this is the last fragment.</param>
    protected abstract void WriteFragmentHeader(
        Span<byte> header,
        ReadOnlySpan<byte> nalHeader,
        bool first,
        bool last
    );

    // Sends a run: one unit alone as a single-NAL-unit packet, several as an aggregation packet.
    private void Flush(ReadOnlySpan<byte> run, int count, int payloadBytes, RtpPayloadWriter writer)
    {
        if (count == 0)
        {
            return;
        }

        if (count == 1)
        {
            run.CopyTo(writer.Add(run.Length));
            return;
        }

        Span<byte> packet = writer.Add(_nalHeaderSize + payloadBytes);
        WriteAggregationHeader(packet[.._nalHeaderSize], run);
        int offset = _nalHeaderSize;
        foreach (Range range in new AnnexBNalUnits(run, startsWithNal: true))
        {
            ReadOnlySpan<byte> nal = run[range];
            packet[offset] = (byte)(nal.Length >> 8);
            packet[offset + 1] = (byte)nal.Length;
            nal.CopyTo(packet[(offset + LengthSize)..]);
            offset += LengthSize + nal.Length;
        }
    }

    private void Fragment(ReadOnlySpan<byte> nal, RtpPayloadWriter writer)
    {
        ReadOnlySpan<byte> header = nal[.._nalHeaderSize];
        ReadOnlySpan<byte> data = nal[_nalHeaderSize..];
        int maxFragment = _maxPayloadSize - _fragmentHeaderSize;
        for (int offset = 0; offset < data.Length; )
        {
            int chunk = Math.Min(maxFragment, data.Length - offset);
            Span<byte> packet = writer.Add(_fragmentHeaderSize + chunk);
            WriteFragmentHeader(
                packet[.._fragmentHeaderSize],
                header,
                first: offset == 0,
                last: offset + chunk == data.Length
            );
            data.Slice(offset, chunk).CopyTo(packet[_fragmentHeaderSize..]);
            offset += chunk;
        }
    }
}

/// <summary>
/// The NAL units of an Annex-B byte stream, as ranges of their bytes without start codes or trailing zero
/// bytes. Enumerating allocates nothing.
/// </summary>
/// <param name="stream">The byte stream.</param>
/// <param name="startsWithNal">True when the stream opens with a NAL unit's first byte and no start code.</param>
internal readonly ref struct AnnexBNalUnits(ReadOnlySpan<byte> stream, bool startsWithNal = false)
{
    private readonly ReadOnlySpan<byte> _stream = stream;
    private readonly bool _startsWithNal = startsWithNal;

    public Enumerator GetEnumerator() => new(_stream, _startsWithNal);

    public ref struct Enumerator
    {
        private readonly ReadOnlySpan<byte> _stream;
        private int _next;

        internal Enumerator(ReadOnlySpan<byte> stream, bool startsWithNal)
        {
            _stream = stream;
            _next = startsWithNal ? 0 : NextStart(stream, 0);
        }

        public Range Current { get; private set; }

        public bool MoveNext()
        {
            if (_next < 0 || _next >= _stream.Length)
            {
                return false;
            }

            int start = _next;
            int following = NextStart(_stream, start);
            int end = following < 0 ? _stream.Length : following - 3;
            _next = following;

            // A NAL unit ends in its stop bit or cabac_zero_words (00 00 03), so zeros before the next
            // start code are the stream's own padding and the extra byte of a four-byte start code.
            while (end > start && _stream[end - 1] == 0)
            {
                end--;
            }

            Current = start..end;
            return true;
        }

        // The index just past the next 00 00 01 at or after from, or -1.
        private static int NextStart(ReadOnlySpan<byte> stream, int from)
        {
            ReadOnlySpan<byte> startCode = [0, 0, 1];
            int found = stream[from..].IndexOf(startCode);
            return found < 0 ? -1 : from + found + startCode.Length;
        }
    }
}

/// <summary>
/// The NAL units of an aggregation packet body: each a 16-bit big-endian size and the unit. Enumeration
/// stops at an empty unit or one whose size runs past the end.
/// </summary>
/// <param name="body">The packet after its aggregation header.</param>
internal readonly ref struct AggregatedNalUnits(ReadOnlySpan<byte> body)
{
    private readonly ReadOnlySpan<byte> _body = body;

    public Enumerator GetEnumerator() => new(_body);

    public ref struct Enumerator(ReadOnlySpan<byte> body)
    {
        private ReadOnlySpan<byte> _rest = body;

        public ReadOnlySpan<byte> Current { get; private set; }

        public bool MoveNext()
        {
            if (_rest.Length < 2)
            {
                return false;
            }

            int size = (_rest[0] << 8) | _rest[1];
            if (size == 0 || size > _rest.Length - 2)
            {
                _rest = [];
                return false;
            }

            Current = _rest.Slice(2, size);
            _rest = _rest[(2 + size)..];
            return true;
        }
    }
}

/// <summary>
/// Builds an Annex-B access unit from NAL units that arrive whole or in fragments, into a buffer rented
/// from <see cref="ArrayPool{T}.Shared"/> that passes to the caller on completion.
/// </summary>
internal sealed class AnnexBAssembler : IDisposable
{
    private const int InitialSize = 64 * 1024;
    private static ReadOnlySpan<byte> StartCode => [0, 0, 0, 1];

    private byte[] _accessUnit = ArrayPool<byte>.Shared.Rent(InitialSize);
    private int _length;
    private int _fragmentStart = -1;

    /// <summary>Appends a whole NAL unit.</summary>
    public void AppendNal(ReadOnlySpan<byte> nal)
    {
        AbandonFragment();
        Append(StartCode);
        Append(nal);
    }

    /// <summary>Starts a fragmented NAL unit with its rebuilt header, dropping an unfinished one.</summary>
    public void StartFragment(ReadOnlySpan<byte> header)
    {
        AbandonFragment();
        _fragmentStart = _length;
        Append(StartCode);
        Append(header);
    }

    /// <summary>Appends a fragment's data; ignored when no fragmented NAL unit was started.</summary>
    public void AppendFragment(ReadOnlySpan<byte> data)
    {
        if (_fragmentStart >= 0)
        {
            Append(data);
        }
    }

    /// <summary>Ends the fragmented NAL unit, keeping it in the access unit.</summary>
    public void EndFragment() => _fragmentStart = -1;

    /// <summary>
    /// When <paramref name="marker"/> is set, hands over the access unit and starts the next; a fragmented
    /// NAL unit left unfinished is dropped from it.
    /// </summary>
    public bool TryComplete(bool marker, out EncodedFrameBuffer frame)
    {
        if (!marker)
        {
            frame = default;
            return false;
        }

        AbandonFragment();
        frame = new EncodedFrameBuffer(_accessUnit, _length);
        _accessUnit = ArrayPool<byte>.Shared.Rent(Math.Max(InitialSize, _length));
        _length = 0;
        return true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_accessUnit.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_accessUnit);
            _accessUnit = [];
        }
    }

    // A fragmented NAL unit that another unit or the end of the access unit interrupts lost its end.
    private void AbandonFragment()
    {
        if (_fragmentStart >= 0)
        {
            _length = _fragmentStart;
            _fragmentStart = -1;
        }
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_length + data.Length > _accessUnit.Length)
        {
            byte[] grown = ArrayPool<byte>.Shared.Rent(
                Math.Max(_accessUnit.Length * 2, _length + data.Length)
            );
            _accessUnit.AsSpan(0, _length).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(_accessUnit);
            _accessUnit = grown;
        }

        data.CopyTo(_accessUnit.AsSpan(_length));
        _length += data.Length;
    }
}
