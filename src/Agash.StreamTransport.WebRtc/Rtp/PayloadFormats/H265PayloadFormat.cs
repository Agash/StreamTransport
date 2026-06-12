using System.Buffers;

namespace Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

/// <summary>
/// Packetizes an HEVC (H.265) access unit into RTP payloads per RFC 7798: each NAL unit is sent as a
/// single-NAL-unit packet when it fits the MTU, or split into Fragmentation Units (FUs, type 49) when it
/// does not. Aggregation Packets and the DON fields are not produced (we do not reorder). The caller sets
/// the RTP marker bit on the last payload of the access unit.
/// </summary>
public static class H265Packetizer
{
    private const int FragmentationUnitType = 49;
    private const int PayloadHeaderSize = 2;
    private const int FuHeaderSize = 1;

    /// <summary>
    /// Splits an Annex-B access unit (NAL units prefixed by start codes) into RTP payloads, each at most
    /// <paramref name="maxPayloadSize"/> bytes, writing them into <paramref name="writer"/> with no per-payload
    /// allocation. The last produced payload is the end of the access unit. Single pass over the input, so no
    /// intermediate range list is allocated.
    /// </summary>
    /// <param name="annexBAccessUnit">The encoded access unit, NAL units separated by Annex-B start codes.</param>
    /// <param name="writer">Reusable destination storage; it is reset before writing this frame's payloads.</param>
    /// <param name="maxPayloadSize">The maximum RTP payload size in bytes.</param>
    public static void Packetize(ReadOnlySpan<byte> annexBAccessUnit, RtpPayloadWriter writer, int maxPayloadSize = 1100)
    {
        writer.Reset();

        int i = 0;
        int nalStart = -1;
        while (i + 2 < annexBAccessUnit.Length)
        {
            if (annexBAccessUnit[i] == 0 && annexBAccessUnit[i + 1] == 0 && annexBAccessUnit[i + 2] == 1)
            {
                if (nalStart >= 0)
                {
                    int end = i;
                    if (end > 0 && annexBAccessUnit[end - 1] == 0)
                    {
                        end--;
                    }

                    PacketizeNal(annexBAccessUnit[nalStart..end], writer, maxPayloadSize);
                }

                i += 3;
                nalStart = i;
            }
            else
            {
                i++;
            }
        }

        if (nalStart >= 0 && nalStart < annexBAccessUnit.Length)
        {
            PacketizeNal(annexBAccessUnit[nalStart..], writer, maxPayloadSize);
        }
    }

    private static void PacketizeNal(ReadOnlySpan<byte> nal, RtpPayloadWriter writer, int maxPayloadSize)
    {
        if (nal.Length < PayloadHeaderSize)
        {
            return;
        }

        if (nal.Length <= maxPayloadSize)
        {
            nal.CopyTo(writer.Add(nal.Length)); // single NAL unit packet
            return;
        }

        // Fragmentation Units: PayloadHdr (NAL header with type rewritten to 49) + FU header + data.
        int nalType = (nal[0] >> 1) & 0x3F;
        byte payloadHdr0 = (byte)((nal[0] & 0x81) | (FragmentationUnitType << 1));
        byte payloadHdr1 = nal[1];
        ReadOnlySpan<byte> nalData = nal[PayloadHeaderSize..];

        int maxFragment = maxPayloadSize - PayloadHeaderSize - FuHeaderSize;
        int offset = 0;
        bool first = true;
        while (offset < nalData.Length)
        {
            int chunk = Math.Min(maxFragment, nalData.Length - offset);
            bool last = offset + chunk >= nalData.Length;

            Span<byte> packet = writer.Add(PayloadHeaderSize + FuHeaderSize + chunk);
            packet[0] = payloadHdr0;
            packet[1] = payloadHdr1;
            packet[2] = (byte)((first ? 0x80 : 0) | (last ? 0x40 : 0) | nalType); // S|E|FuType
            nalData.Slice(offset, chunk).CopyTo(packet[3..]);

            offset += chunk;
            first = false;
        }
    }
}

/// <summary>
/// Reassembles HEVC access units from received RTP payloads (RFC 7798): single-NAL packets, Aggregation
/// Packets (type 48), and Fragmentation Units (type 49). Stateful — feed payloads in order and it emits a
/// complete Annex-B access unit when a payload carrying the RTP marker bit is pushed. The completed access
/// unit is returned in a buffer rented from <see cref="ArrayPool{T}.Shared"/> whose ownership passes to the
/// caller, so the frame can cross to a decode worker with no per-frame allocation.
/// </summary>
public sealed class H265Depacketizer : IDisposable
{
    private static readonly byte[] StartCode = [0, 0, 0, 1];
    private byte[] _accessUnit = ArrayPool<byte>.Shared.Rent(64 * 1024);
    private int _accessUnitLength;
    private byte[] _fragment = new byte[64 * 1024];
    private int _fragmentLength;

    /// <summary>
    /// Pushes one RTP payload and its marker bit. When <paramref name="marker"/> is set (end of frame) returns
    /// the assembled Annex-B access unit in a pool-rented <paramref name="accessUnit"/> of <paramref name="length"/>
    /// bytes, whose ownership passes to the caller (return it to <see cref="ArrayPool{T}.Shared"/> when done);
    /// otherwise returns false while still assembling.
    /// </summary>
    /// <param name="payload">The received RTP payload (borrowed for this call).</param>
    /// <param name="marker">The RTP marker bit, set on the last payload of an access unit.</param>
    /// <param name="accessUnit">On return true, the pool-rented buffer holding the assembled access unit.</param>
    /// <param name="length">On return true, the number of valid bytes at the start of <paramref name="accessUnit"/>.</param>
    /// <returns>True when a complete access unit was produced; false while still assembling.</returns>
    public bool Push(ReadOnlySpan<byte> payload, bool marker, out byte[] accessUnit, out int length)
    {
        if (payload.Length >= 2)
        {
            int type = (payload[0] >> 1) & 0x3F;
            switch (type)
            {
                case 49:
                    HandleFragmentationUnit(payload);
                    break;
                case 48:
                    HandleAggregationPacket(payload);
                    break;
                default:
                    AppendNal(payload);
                    break;
            }
        }

        if (!marker)
        {
            accessUnit = [];
            length = 0;
            return false;
        }

        // Transfer ownership of the assembled buffer to the caller and rent a fresh one for the next frame.
        accessUnit = _accessUnit;
        length = _accessUnitLength;
        _accessUnit = ArrayPool<byte>.Shared.Rent(Math.Max(64 * 1024, _accessUnitLength));
        _accessUnitLength = 0;
        return true;
    }

    private void HandleFragmentationUnit(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3)
        {
            return;
        }

        byte fuHeader = payload[2];
        bool start = (fuHeader & 0x80) != 0;
        bool end = (fuHeader & 0x40) != 0;
        int fuType = fuHeader & 0x3F;

        if (start)
        {
            _fragmentLength = 0;
            // Rebuild the NAL header from the PayloadHdr, restoring the original type.
            AppendFragment((byte)((payload[0] & 0x81) | (fuType << 1)));
            AppendFragment(payload[1]);
        }

        AppendFragment(payload[3..]);

        if (end && _fragmentLength >= 2)
        {
            AppendNal(_fragment.AsSpan(0, _fragmentLength));
            _fragmentLength = 0;
        }
    }

    private void HandleAggregationPacket(ReadOnlySpan<byte> payload)
    {
        int offset = 2; // skip the 2-byte PayloadHdr
        while (offset + 2 <= payload.Length)
        {
            int size = (payload[offset] << 8) | payload[offset + 1];
            offset += 2;
            if (size <= 0 || offset + size > payload.Length)
            {
                break;
            }

            AppendNal(payload.Slice(offset, size));
            offset += size;
        }
    }

    private void AppendNal(ReadOnlySpan<byte> nal)
    {
        EnsureAccessUnitCapacity(StartCode.Length + nal.Length);
        StartCode.CopyTo(_accessUnit.AsSpan(_accessUnitLength));
        _accessUnitLength += StartCode.Length;
        nal.CopyTo(_accessUnit.AsSpan(_accessUnitLength));
        _accessUnitLength += nal.Length;
    }

    private void EnsureAccessUnitCapacity(int additional)
    {
        if (_accessUnitLength + additional <= _accessUnit.Length)
        {
            return;
        }

        byte[] grown = ArrayPool<byte>.Shared.Rent(Math.Max(_accessUnit.Length * 2, _accessUnitLength + additional));
        _accessUnit.AsSpan(0, _accessUnitLength).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_accessUnit);
        _accessUnit = grown;
    }

    private void AppendFragment(byte value)
    {
        if (_fragmentLength + 1 > _fragment.Length)
        {
            Array.Resize(ref _fragment, _fragment.Length * 2);
        }

        _fragment[_fragmentLength++] = value;
    }

    private void AppendFragment(ReadOnlySpan<byte> data)
    {
        if (_fragmentLength + data.Length > _fragment.Length)
        {
            Array.Resize(ref _fragment, Math.Max(_fragment.Length * 2, _fragmentLength + data.Length));
        }

        data.CopyTo(_fragment.AsSpan(_fragmentLength));
        _fragmentLength += data.Length;
    }

    /// <summary>Returns the in-flight assembly buffer to the shared pool.</summary>
    public void Dispose()
    {
        if (_accessUnit.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_accessUnit);
            _accessUnit = [];
        }
    }
}
