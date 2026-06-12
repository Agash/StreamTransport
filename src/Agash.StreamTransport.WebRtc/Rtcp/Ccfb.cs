using System.Buffers.Binary;

namespace Agash.StreamTransport.WebRtc.Rtcp;

/// <summary>Per-packet arrival metric in an RFC 8888 feedback report (R bit, 2-bit ECN, 13-bit arrival-time offset).</summary>
/// <param name="Received">Whether the packet was received.</param>
/// <param name="Ecn">The echoed 2-bit ECN mark.</param>
/// <param name="ArrivalTimeOffset">Arrival time before the Report Timestamp, in units of 1/1024 s (13 bits); 0x1FFF = unknown.</param>
public readonly record struct CcfbMetric(bool Received, byte Ecn, ushort ArrivalTimeOffset);

/// <summary>An RFC 8888 per-SSRC report block: the metrics for a run of sequence numbers from <see cref="BeginSequence"/>.</summary>
public readonly record struct CcfbStreamReport(uint Ssrc, ushort BeginSequence, IReadOnlyList<CcfbMetric> Metrics);

/// <summary>
/// Builds and parses the RFC 8888 RTCP Congestion Control Feedback packet (PT 205, FMT 11) — the modern,
/// standardized transport feedback this stack uses instead of the legacy transport-wide-cc (TWCC). The
/// receiver reports per-packet arrival times + ECN keyed on the media RTP sequence number; the sender's
/// congestion controller consumes them.
/// </summary>
public static class Ccfb
{
    private const int Fmt = 11;

    /// <summary>The "received but arrival time unavailable / out of range" ATO sentinel (RFC 8888 §3.1).</summary>
    public const ushort ArrivalTimeUnknown = 0x1FFF;

    /// <summary>Builds a CCFB packet. Returns the bytes written.</summary>
    public static int Build(Span<byte> destination, uint senderSsrc, IReadOnlyList<CcfbStreamReport> streams, uint reportTimestamp)
    {
        destination[0] = 0x80 | Fmt; // V=2, P=0, FMT=11
        destination[1] = (byte)RtcpPacketType.TransportFeedback; // 205
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], senderSsrc);

        int offset = 8;
        foreach (CcfbStreamReport stream in streams)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination[offset..], stream.Ssrc);
            BinaryPrimitives.WriteUInt16BigEndian(destination[(offset + 4)..], stream.BeginSequence);
            BinaryPrimitives.WriteUInt16BigEndian(destination[(offset + 6)..], (ushort)stream.Metrics.Count);
            offset += 8;

            foreach (CcfbMetric metric in stream.Metrics)
            {
                ushort value = (ushort)((metric.Received ? 0x8000 : 0) | ((metric.Ecn & 0x3) << 13) | (metric.ArrivalTimeOffset & 0x1FFF));
                BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], value);
                offset += 2;
            }

            if (stream.Metrics.Count % 2 != 0)
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
    public static bool TryParse(ReadOnlySpan<byte> packet, out uint senderSsrc, out uint reportTimestamp, List<CcfbStreamReport> streams)
    {
        senderSsrc = 0;
        reportTimestamp = 0;
        foreach (RtcpElement element in RtcpCompound.Enumerate(packet))
        {
            if (element.PacketType != RtcpPacketType.TransportFeedback || element.ReportCount != Fmt || element.Body.Length < 8)
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

                var metrics = new CcfbMetric[numReports];
                for (int i = 0; i < numReports && o + 2 <= end; i++, o += 2)
                {
                    ushort value = BinaryPrimitives.ReadUInt16BigEndian(body[o..]);
                    metrics[i] = new CcfbMetric((value & 0x8000) != 0, (byte)((value >> 13) & 0x3), (ushort)(value & 0x1FFF));
                }

                if (numReports % 2 != 0)
                {
                    o += 2; // skip pad
                }

                streams.Add(new CcfbStreamReport(ssrc, beginSeq, metrics));
            }

            reportTimestamp = BinaryPrimitives.ReadUInt32BigEndian(body[end..]);
            return true;
        }

        return false;
    }
}
