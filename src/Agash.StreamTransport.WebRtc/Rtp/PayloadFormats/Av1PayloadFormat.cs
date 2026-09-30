using System.Buffers;
using System.Collections.Immutable;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

/// <summary>The AV1 RTP payload format (AOM "RTP Payload Format for AV1"), profile 0 by default.</summary>
public sealed class Av1PayloadFormat : RtpPayloadFormat
{
    private Av1PayloadFormat() { }

    /// <summary>The format.</summary>
    public static Av1PayloadFormat Instance { get; } = new();

    /// <inheritdoc/>
    public override string EncodingName => "AV1";

    /// <inheritdoc/>
    public override SdpMediaKind Kind => SdpMediaKind.Video;

    /// <inheritdoc/>
    public override int ClockRate => 90_000;

    /// <inheritdoc/>
    public override ImmutableArray<string> RtcpFeedback => ["nack", "nack pli"];

    /// <inheritdoc/>
    public override RtpPayloadTraits KeyframeRequires => RtpPayloadTraits.SequenceParameters;

    /// <inheritdoc/>
    public override IRtpPacketizer CreatePacketizer(int maxPayloadSize) =>
        new Av1Packetizer(maxPayloadSize);

    /// <inheritdoc/>
    public override IRtpDepacketizer CreateDepacketizer() => new Av1Depacketizer();

    /// <inheritdoc/>
    /// <remarks>
    /// The N bit opens a coded video sequence, which starts with a key frame. A sequence header is found
    /// among the elements that begin in the payload.
    /// </remarks>
    public override RtpPayloadTraits Inspect(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return RtpPayloadTraits.None;
        }

        RtpPayloadTraits traits =
            (payload[0] & 0x08) != 0
                ? RtpPayloadTraits.SequenceStart | RtpPayloadTraits.Keyframe
                : RtpPayloadTraits.None;
        bool continuation = (payload[0] & 0x80) != 0;
        foreach (ReadOnlySpan<byte> element in new Av1Elements(payload))
        {
            if (!continuation && !element.IsEmpty && Obu.TypeOf(element[0]) == Obu.SequenceHeader)
            {
                traits |= RtpPayloadTraits.SequenceParameters;
            }

            continuation = false;
        }

        return traits;
    }
}

/// <summary>
/// Packetizes AV1 temporal units per the AOM "RTP Payload Format for AV1": OBUs travel without their
/// size fields as elements behind a one-byte aggregation header, several to a packet when they fit, and
/// an OBU larger than the room left continues in the next packet. Temporal delimiters, tile lists and
/// padding are left out, as the format requires.
/// </summary>
public sealed class Av1Packetizer : IRtpPacketizer
{
    private const int AggregationHeaderSize = 1;

    private readonly int _maxPayloadSize;

    /// <summary>A packetizer producing payloads of at most <paramref name="maxPayloadSize"/> bytes.</summary>
    /// <param name="maxPayloadSize">The largest RTP payload in bytes; at least 3.</param>
    public Av1Packetizer(int maxPayloadSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadSize, AggregationHeaderSize + 2);
        _maxPayloadSize = maxPayloadSize;
    }

    /// <summary>Splits a temporal unit in the low-overhead bitstream format into RTP payloads.</summary>
    /// <param name="frame">The temporal unit: OBUs, each but the last with a size field.</param>
    /// <param name="writer">Reusable payload storage; reset before writing.</param>
    /// <exception cref="InvalidDataException">An OBU runs past the end of the temporal unit.</exception>
    public void Packetize(ReadOnlySpan<byte> frame, RtpPayloadWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Reset();

        int count = Obu.Count(frame);
        Obu[] obus = ArrayPool<Obu>.Shared.Rent(Math.Max(count, 1));
        Element[] elements = ArrayPool<Element>.Shared.Rent(count + 1);
        try
        {
            int kept = 0;
            bool sequenceHeader = false;
            foreach (Obu obu in Obu.Parse(frame, obus.AsSpan(0, count)))
            {
                if (obu.Type is not (Obu.TemporalDelimiter or Obu.TileList or Obu.Padding))
                {
                    obus[kept++] = obu;
                    sequenceHeader |= obu.Type == Obu.SequenceHeader;
                }
            }

            WritePackets(frame, obus.AsSpan(0, kept), elements, sequenceHeader, writer);
        }
        finally
        {
            ArrayPool<Obu>.Shared.Return(obus);
            ArrayPool<Element>.Shared.Return(elements);
        }
    }

    // Plans each packet with every element length-prefixed, then writes it; a packet of up to three
    // elements states its count in W and leaves the last length out, so it only ever comes out smaller.
    private void WritePackets(
        ReadOnlySpan<byte> temporalUnit,
        ReadOnlySpan<Obu> obus,
        Element[] elements,
        bool sequenceHeader,
        RtpPayloadWriter writer
    )
    {
        int obuIndex = 0;
        int obuOffset = 0;
        bool continues = false;
        bool first = true;
        while (obuIndex < obus.Length)
        {
            int room = _maxPayloadSize - AggregationHeaderSize;
            int planned = 0;
            bool fragmentEnds = false;
            while (obuIndex < obus.Length)
            {
                int remaining = obus[obuIndex].ElementSize - obuOffset;
                int whole = Leb128.Size((uint)remaining) + remaining;
                if (whole <= room)
                {
                    elements[planned++] = new Element(obuIndex, obuOffset, remaining);
                    room -= whole;
                    obuIndex++;
                    obuOffset = 0;
                    continue;
                }

                int part = Leb128.LargestFitting(room);
                if (part > 0)
                {
                    elements[planned++] = new Element(obuIndex, obuOffset, part);
                    obuOffset += part;
                    fragmentEnds = true;
                }

                break;
            }

            int withW = planned <= 3 ? planned : 0;
            int size = AggregationHeaderSize;
            for (int i = 0; i < planned; i++)
            {
                bool sized = withW == 0 || i < withW - 1;
                size += (sized ? Leb128.Size((uint)elements[i].Length) : 0) + elements[i].Length;
            }

            Span<byte> packet = writer.Add(size);
            packet[0] = (byte)(
                (continues ? 0x80 : 0)
                | (fragmentEnds ? 0x40 : 0)
                | (withW << 4)
                | (first && sequenceHeader ? 0x08 : 0)
            );
            int offset = AggregationHeaderSize;
            for (int i = 0; i < planned; i++)
            {
                Element element = elements[i];
                if (withW == 0 || i < withW - 1)
                {
                    offset += Leb128.Write((uint)element.Length, packet[offset..]);
                }

                obus[element.Obu]
                    .CopyElement(temporalUnit, element.Offset, packet.Slice(offset, element.Length));
                offset += element.Length;
            }

            continues = fragmentEnds;
            first = false;
        }
    }

    private readonly record struct Element(int Obu, int Offset, int Length);
}

/// <summary>
/// Reassembles AV1 temporal units per the AOM "RTP Payload Format for AV1", fed in sequence order. The
/// temporal unit comes out on the marker bit in the low-overhead bitstream format, opened by a temporal
/// delimiter and with a size field on every OBU. An OBU missing its first or last fragment is dropped.
/// </summary>
public sealed class Av1Depacketizer : IRtpDepacketizer
{
    private const int InitialSize = 64 * 1024;

    private byte[] _temporalUnit = ArrayPool<byte>.Shared.Rent(InitialSize);
    private int _length;
    private byte[] _fragment = ArrayPool<byte>.Shared.Rent(InitialSize);
    private int _fragmentLength = -1;

    /// <inheritdoc/>
    public bool TryPush(ReadOnlySpan<byte> payload, bool marker, out EncodedFrameBuffer frame)
    {
        if (!payload.IsEmpty)
        {
            ReadElements(payload);
        }

        if (!marker)
        {
            frame = default;
            return false;
        }

        _fragmentLength = -1;
        frame = new EncodedFrameBuffer(_temporalUnit, _length);
        _temporalUnit = ArrayPool<byte>.Shared.Rent(Math.Max(InitialSize, _length));
        _length = 0;
        return true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_temporalUnit.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_temporalUnit);
            ArrayPool<byte>.Shared.Return(_fragment);
            _temporalUnit = [];
            _fragment = [];
        }
    }

    private void ReadElements(ReadOnlySpan<byte> payload)
    {
        bool continuesFromLast = (payload[0] & 0x80) != 0;
        bool continuesIntoNext = (payload[0] & 0x40) != 0;
        Av1Elements.Enumerator elements = new Av1Elements(payload).GetEnumerator();
        bool first = true;
        while (elements.MoveNext())
        {
            ReadOnlySpan<byte> element = elements.Current;
            bool endsWithFragment = elements.IsLast && continuesIntoNext;
            if (first && continuesFromLast)
            {
                // A continuation with no fragment in progress lost its first part.
                if (_fragmentLength >= 0)
                {
                    AppendFragment(element);
                    if (!endsWithFragment)
                    {
                        AppendObu(_fragment.AsSpan(0, _fragmentLength));
                        _fragmentLength = -1;
                    }
                }
            }
            else
            {
                // A fragment still in progress here lost its last part.
                _fragmentLength = endsWithFragment ? 0 : -1;
                if (endsWithFragment)
                {
                    AppendFragment(element);
                }
                else
                {
                    AppendObu(element);
                }
            }

            first = false;
        }

        if (elements.Malformed)
        {
            _fragmentLength = -1;
        }
    }

    // Writes one OBU element with its size field set, after the temporal delimiter that opens the unit.
    private void AppendObu(ReadOnlySpan<byte> element)
    {
        if (element.IsEmpty)
        {
            return;
        }

        int headerSize = (element[0] & 0x04) != 0 ? 2 : 1;
        if (element.Length < headerSize)
        {
            return;
        }

        if (_length == 0)
        {
            Append([(Obu.TemporalDelimiter << 3) | 0x02, 0x00]);
        }

        ReadOnlySpan<byte> body = element[headerSize..];
        Span<byte> prefix = stackalloc byte[2 + Leb128.MaxSize];
        prefix[0] = (byte)(element[0] | 0x02);
        if (headerSize == 2)
        {
            prefix[1] = element[1];
        }

        int prefixSize = headerSize + Leb128.Write((uint)body.Length, prefix[headerSize..]);
        Append(prefix[..prefixSize]);
        Append(body);
    }

    private void Append(ReadOnlySpan<byte> data) => Grow(ref _temporalUnit, ref _length, data);

    private void AppendFragment(ReadOnlySpan<byte> data) =>
        Grow(ref _fragment, ref _fragmentLength, data);

    private static void Grow(ref byte[] buffer, ref int length, ReadOnlySpan<byte> data)
    {
        if (length + data.Length > buffer.Length)
        {
            byte[] grown = ArrayPool<byte>.Shared.Rent(
                Math.Max(buffer.Length * 2, length + data.Length)
            );
            buffer.AsSpan(0, length).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = grown;
        }

        data.CopyTo(buffer.AsSpan(length));
        length += data.Length;
    }
}

/// <summary>
/// The OBU elements of an AV1 RTP payload: each behind its LEB128 length, except the last when the
/// aggregation header's W field counts the elements. Enumeration stops at a length past the end.
/// </summary>
/// <param name="payload">The payload, aggregation header included.</param>
internal readonly ref struct Av1Elements(ReadOnlySpan<byte> payload)
{
    private readonly ReadOnlySpan<byte> _payload = payload;

    public Enumerator GetEnumerator() => new(_payload);

    public ref struct Enumerator
    {
        private readonly int _count;
        private ReadOnlySpan<byte> _rest;
        private int _index;

        internal Enumerator(ReadOnlySpan<byte> payload)
        {
            _count = payload.IsEmpty ? 0 : (payload[0] >> 4) & 0x3;
            _rest = payload.IsEmpty ? [] : payload[1..];
        }

        public ReadOnlySpan<byte> Current { get; private set; }

        // Whether Current is the payload's last element.
        public readonly bool IsLast => _rest.IsEmpty;

        // Whether enumeration stopped at a length running past the end.
        public bool Malformed { get; private set; }

        public bool MoveNext()
        {
            if (_rest.IsEmpty)
            {
                return false;
            }

            if (_count != 0 && _index == _count - 1)
            {
                Current = _rest;
                _rest = [];
            }
            else if (
                Leb128.TryRead(_rest, out uint size, out int read)
                && size <= (uint)(_rest.Length - read)
            )
            {
                Current = _rest.Slice(read, (int)size);
                _rest = _rest[(read + (int)size)..];
            }
            else
            {
                Malformed = true;
                _rest = [];
                return false;
            }

            _index++;
            return true;
        }
    }
}

/// <summary>An OBU located in a temporal unit: its header bytes and where its payload lies.</summary>
internal readonly record struct Obu(byte Header, byte Extension, int PayloadStart, int PayloadLength)
{
    public const int SequenceHeader = 1;
    public const int TemporalDelimiter = 2;
    public const int TileList = 8;
    public const int Padding = 15;

    public int Type => TypeOf(Header);

    public bool HasExtension => (Header & 0x04) != 0;

    // Its size as an RTP element: the header without a size field, then the payload.
    public int ElementSize => (HasExtension ? 2 : 1) + PayloadLength;

    public static int TypeOf(byte header) => (header >> 3) & 0xF;

    // Copies part of the element into a packet, clearing the size-field flag in the header.
    public void CopyElement(ReadOnlySpan<byte> temporalUnit, int offset, Span<byte> destination)
    {
        int headerSize = HasExtension ? 2 : 1;
        ReadOnlySpan<byte> header = [(byte)(Header & ~0x02), Extension];
        int written = 0;
        for (; offset < headerSize && written < destination.Length; offset++, written++)
        {
            destination[written] = header[offset];
        }

        temporalUnit
            .Slice(PayloadStart + offset - headerSize, destination.Length - written)
            .CopyTo(destination[written..]);
    }

    public static int Count(ReadOnlySpan<byte> temporalUnit)
    {
        int count = 0;
        for (int offset = 0; offset < temporalUnit.Length; count++)
        {
            offset = Next(temporalUnit, offset).End;
        }

        return count;
    }

    public static ReadOnlySpan<Obu> Parse(ReadOnlySpan<byte> temporalUnit, Span<Obu> into)
    {
        int offset = 0;
        for (int i = 0; i < into.Length; i++)
        {
            (into[i], offset) = Next(temporalUnit, offset);
        }

        return into;
    }

    private static (Obu Obu, int End) Next(ReadOnlySpan<byte> temporalUnit, int offset)
    {
        byte header = temporalUnit[offset];
        bool extension = (header & 0x04) != 0;
        bool sized = (header & 0x02) != 0;
        int position = offset + 1 + (extension ? 1 : 0);
        if (position > temporalUnit.Length)
        {
            throw new InvalidDataException("An OBU header runs past the end of the temporal unit.");
        }

        byte extensionByte = extension ? temporalUnit[offset + 1] : (byte)0;
        int length = temporalUnit.Length - position;
        if (sized)
        {
            if (
                !Leb128.TryRead(temporalUnit[position..], out uint size, out int read)
                || size > (uint)(temporalUnit.Length - position - read)
            )
            {
                throw new InvalidDataException(
                    "An OBU's size runs past the end of the temporal unit."
                );
            }

            position += read;
            length = (int)size;
        }

        return (new Obu(header, extensionByte, position, length), position + length);
    }
}

/// <summary>Unsigned LEB128, the variable-length integers of AV1 and its RTP payload format.</summary>
internal static class Leb128
{
    public const int MaxSize = 5;

    public static int Size(uint value)
    {
        int size = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            size++;
        }

        return size;
    }

    public static int Write(uint value, Span<byte> destination)
    {
        int i = 0;
        while (value >= 0x80)
        {
            destination[i++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[i++] = (byte)value;
        return i;
    }

    public static bool TryRead(ReadOnlySpan<byte> source, out uint value, out int read)
    {
        value = 0;
        for (read = 0; read < Math.Min(source.Length, MaxSize); read++)
        {
            value |= (uint)(source[read] & 0x7F) << (7 * read);
            if ((source[read] & 0x80) == 0)
            {
                read++;
                return true;
            }
        }

        return false;
    }

    // The most element bytes that fit in room together with their own length prefix; zero when none do.
    public static int LargestFitting(int room)
    {
        int part = room - Size((uint)Math.Max(room, 0));
        while (part > 0 && Size((uint)part) + part > room)
        {
            part--;
        }

        return Math.Max(part, 0);
    }
}
