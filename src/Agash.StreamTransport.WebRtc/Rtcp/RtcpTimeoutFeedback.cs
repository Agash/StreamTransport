using System.Buffers.Binary;

namespace Agash.StreamTransport.WebRtc.Rtcp;

// What resets the RTCP timeout circuit breaker (RFC 8083 section 4.1): a sender or receiver report
// that carries a reception block about one of this endpoint's sources, any of them, since every
// stream shares the one 5-tuple; or reduced-size RTCP, which carries no reports and counts as received
// (section 5). A compound report whose blocks are all about other sources says nothing about this
// endpoint's media and resets nothing.
internal static class RtcpTimeoutFeedback
{
    public static bool Resets(ReadOnlySpan<byte> rtcp, Func<uint, bool> isOwnSource)
    {
        bool first = true;
        foreach (RtcpElement element in RtcpCompound.Enumerate(rtcp))
        {
            int blocksAt;
            if (element.PacketType == RtcpPacketType.SenderReport)
            {
                blocksAt = 24;
            }
            else if (element.PacketType == RtcpPacketType.ReceiverReport)
            {
                blocksAt = 4;
            }
            else
            {
                // A compound packet starts with a report; one that does not is reduced-size.
                if (first)
                {
                    return true;
                }

                continue;
            }

            first = false;
            ReadOnlySpan<byte> body = element.Body;
            for (
                int i = 0;
                i < element.ReportCount
                    && blocksAt + ((i + 1) * RtcpReportBlock.Length) <= body.Length;
                i++
            )
            {
                if (
                    isOwnSource(
                        BinaryPrimitives.ReadUInt32BigEndian(
                            body[(blocksAt + (i * RtcpReportBlock.Length))..]
                        )
                    )
                )
                {
                    return true;
                }
            }
        }

        return false;
    }
}
