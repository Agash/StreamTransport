using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Agash.StreamTransport.WebRtc.Rtcp;

/// <summary>Per-packet arrival metric in an RFC 8888 feedback report (R bit, 2-bit ECN, 13-bit arrival-time offset).</summary>
/// <param name="Received">Whether the packet was received.</param>
/// <param name="Ecn">The echoed 2-bit ECN mark.</param>
/// <param name="ArrivalTimeOffset">Arrival time before the Report Timestamp, in units of 1/1024 s (13 bits); 0x1FFF = unknown.</param>
public readonly record struct CcfbMetric(bool Received, byte Ecn, ushort ArrivalTimeOffset);

/// <summary>An RFC 8888 per-SSRC report block: the metrics for a run of sequence numbers from <see cref="BeginSequence"/>.</summary>
/// <remarks>
/// <see cref="Metrics"/> is a slice of the <see cref="CcfbMetricStore"/> the block was built or parsed into,
/// valid until that store is cleared.
/// </remarks>
public readonly record struct CcfbStreamReport(
    uint Ssrc,
    ushort BeginSequence,
    ReadOnlyMemory<CcfbMetric> Metrics
);

/// <summary>
/// The metrics of the report blocks being built or parsed, kept from one report to the next so that
/// reporting, once warm, allocates nothing. A block's metrics stay where they are until
/// <see cref="Clear"/>.
/// </summary>
public sealed class CcfbMetricStore
{
    private const int ChunkSize = 1024;

    private readonly List<CcfbMetric[]> _chunks = [];
    private int _chunk;
    private int _used;

    /// <summary>Room for a block's metrics, after those already handed out.</summary>
    /// <param name="count">How many.</param>
    /// <returns>The room.</returns>
    public Memory<CcfbMetric> Rent(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        while (_chunk < _chunks.Count && _chunks[_chunk].Length - _used < count)
        {
            _chunk++;
            _used = 0;
        }

        if (_chunk == _chunks.Count)
        {
            _chunks.Add(new CcfbMetric[Math.Max(ChunkSize, count)]);
            _used = 0;
        }

        Memory<CcfbMetric> room = _chunks[_chunk].AsMemory(_used, count);
        _used += count;
        return room;
    }

    /// <summary>Takes back every block's metrics, for the next report.</summary>
    public void Clear()
    {
        _chunk = 0;
        _used = 0;
    }
}

/// <summary>
/// Builds and parses the RFC 8888 RTCP Congestion Control Feedback packet (PT 205, FMT 11), the standard
/// transport feedback and the one this stack prefers; with a peer that offers only transport-wide-cc, as
/// browsers do, it uses <see cref="TransportFeedback"/>. The receiver reports per-packet arrival times and
/// ECN keyed on the media RTP sequence number; the sender's congestion controller consumes them.
/// </summary>
public static class Ccfb
{
    private const int Fmt = 11;

    /// <summary>
    /// The ATO for a packet received but whose arrival time is unavailable or after the report time
    /// (RFC 8888 section 3.1).
    /// </summary>
    public const ushort ArrivalTimeUnknown = 0x1FFF;

    /// <summary>The ATO for a packet that arrived more than 8189/1024 s before the report (section 3.1).</summary>
    public const ushort ArrivalTimeOverRange = 0x1FFE;

    /// <summary>Encodes how long before the report a packet arrived, in 1/1024 s, with the sentinels.</summary>
    /// <param name="ageMicros">The report time less the arrival time, in microseconds.</param>
    /// <returns>The 13-bit ATO.</returns>
    public static ushort ArrivalTimeOffset(long ageMicros)
    {
        if (ageMicros < 0)
        {
            return ArrivalTimeUnknown;
        }

        long units = ageMicros * 1024 / 1_000_000;
        return units >= ArrivalTimeOverRange ? ArrivalTimeOverRange : (ushort)units;
    }

    /// <summary>Builds a CCFB packet. Returns the bytes written.</summary>
    public static int Build(
        Span<byte> destination,
        uint senderSsrc,
        ReadOnlySpan<CcfbStreamReport> streams,
        uint reportTimestamp
    )
    {
        destination[0] = 0x80 | Fmt; // V=2, P=0, FMT=11
        destination[1] = (byte)RtcpPacketType.TransportFeedback; // 205
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], senderSsrc);

        int offset = 8;
        foreach (CcfbStreamReport stream in streams)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination[offset..], stream.Ssrc);
            BinaryPrimitives.WriteUInt16BigEndian(
                destination[(offset + 4)..],
                stream.BeginSequence
            );
            BinaryPrimitives.WriteUInt16BigEndian(
                destination[(offset + 6)..],
                (ushort)stream.Metrics.Length
            );
            offset += 8;

            foreach (CcfbMetric metric in stream.Metrics.Span)
            {
                ushort value = (ushort)(
                    (metric.Received ? 0x8000 : 0)
                    | ((metric.Ecn & 0x3) << 13)
                    | (metric.ArrivalTimeOffset & 0x1FFF)
                );
                BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], value);
                offset += 2;
            }

            if (stream.Metrics.Length % 2 != 0)
            {
                BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], 0); // pad to 32-bit boundary
                offset += 2;
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(destination[offset..], reportTimestamp);
        offset += 4;

        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)((offset / 4) - 1));
        return offset;
    }

    /// <summary>Parses the first CCFB packet in an RTCP compound, populating <paramref name="streams"/>.</summary>
    /// <param name="packet">The RTCP compound.</param>
    /// <param name="senderSsrc">The feedback's sender.</param>
    /// <param name="reportTimestamp">The report's time, the middle 32 bits of NTP time.</param>
    /// <param name="streams">Where the report blocks go.</param>
    /// <param name="metrics">Where the blocks' metrics go.</param>
    /// <returns>Whether the compound held a CCFB packet.</returns>
    public static bool TryParse(
        ReadOnlySpan<byte> packet,
        out uint senderSsrc,
        out uint reportTimestamp,
        List<CcfbStreamReport> streams,
        CcfbMetricStore metrics
    )
    {
        senderSsrc = 0;
        reportTimestamp = 0;
        foreach (RtcpElement element in RtcpCompound.Enumerate(packet))
        {
            if (
                element.PacketType != RtcpPacketType.TransportFeedback
                || element.ReportCount != Fmt
                || element.Body.Length < 8
            )
            {
                continue;
            }

            ReadOnlySpan<byte> body = element.Body;
            senderSsrc = BinaryPrimitives.ReadUInt32BigEndian(body);

            int o = 4;
            int end = body.Length - 4; // last 4 bytes are the report timestamp
            while (o + 8 <= end)
            {
                uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(body[o..]);
                ushort beginSeq = BinaryPrimitives.ReadUInt16BigEndian(body[(o + 4)..]);
                int numReports = BinaryPrimitives.ReadUInt16BigEndian(body[(o + 6)..]);
                o += 8;

                Memory<CcfbMetric> block = metrics.Rent(numReports);
                Span<CcfbMetric> values = block.Span;
                for (int i = 0; i < numReports && o + 2 <= end; i++, o += 2)
                {
                    ushort value = BinaryPrimitives.ReadUInt16BigEndian(body[o..]);
                    values[i] = new CcfbMetric(
                        (value & 0x8000) != 0,
                        (byte)((value >> 13) & 0x3),
                        (ushort)(value & 0x1FFF)
                    );
                }

                if (numReports % 2 != 0)
                {
                    o += 2; // skip pad
                }

                streams.Add(new CcfbStreamReport(ssrc, beginSeq, block));
            }

            reportTimestamp = BinaryPrimitives.ReadUInt32BigEndian(body[end..]);
            return true;
        }

        return false;
    }
}
