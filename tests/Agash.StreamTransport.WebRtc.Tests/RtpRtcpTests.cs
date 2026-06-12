using Agash.StreamTransport.WebRtc.Rtcp;
using Agash.StreamTransport.WebRtc.Rtp;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class RtpRtcpTests
{
    [TestMethod]
    public void RtpPacket_WriteThenParse_RoundTripsHeaderAndPayload()
    {
        byte[] payload = [1, 2, 3, 4, 5, 6, 7];
        byte[] buffer = new byte[64];

        int length = RtpPacket.Write(buffer, marker: true, payloadType: 96, sequenceNumber: 4321,
            timestamp: 0xDEADBEEF, ssrc: 0x11223344, payload);

        Assert.IsTrue(RtpPacket.TryParse(buffer.AsSpan(0, length), out RtpHeader header, out ReadOnlySpan<byte> parsedPayload));
        Assert.IsTrue(header.Marker);
        Assert.AreEqual(96, header.PayloadType);
        Assert.AreEqual(4321, header.SequenceNumber);
        Assert.AreEqual(0xDEADBEEFu, header.Timestamp);
        Assert.AreEqual(0x11223344u, header.Ssrc);
        Assert.IsNull(header.AbsoluteCaptureTimeNtp);
        CollectionAssert.AreEqual(payload, parsedPayload.ToArray());
    }

    [TestMethod]
    public void RtpPacket_WithAbsCaptureTime_RoundTrips()
    {
        const ulong ntp = 0x1234_5678_9ABC_DEF0;
        byte[] payload = [0xAA, 0xBB];
        byte[] buffer = new byte[64];

        int length = RtpPacket.Write(buffer, marker: false, payloadType: 100, sequenceNumber: 7,
            timestamp: 1000, ssrc: 0x55667788, payload, absCaptureTimeExtensionId: 1, absCaptureTimeNtp: ntp);

        Assert.IsTrue(RtpPacket.TryParse(buffer.AsSpan(0, length), out RtpHeader header, out ReadOnlySpan<byte> parsedPayload));
        Assert.AreEqual(ntp, header.AbsoluteCaptureTimeNtp);
        Assert.AreEqual(1000u, header.Timestamp);
        CollectionAssert.AreEqual(payload, parsedPayload.ToArray());
    }

    [TestMethod]
    public void RtpPacket_TryParse_RejectsTooShortOrWrongVersion()
    {
        Assert.IsFalse(RtpPacket.TryParse([0x80, 0x60], out _, out _));
        Assert.IsFalse(RtpPacket.TryParse(new byte[12], out _, out _)); // version 0
    }

    [TestMethod]
    public void SenderReport_WriteThenParse_PreservesNtpRtpMapping()
    {
        var blocks = new RtcpReportBlock[]
        {
            new(Ssrc: 0xAABBCCDD, FractionLost: 5, CumulativeLost: 100, ExtendedHighestSequence: 5000,
                InterarrivalJitter: 42, LastSenderReport: 0x12345678, DelaySinceLastSenderReport: 65536),
        };
        var sr = new RtcpSenderReport(0x11112222, NtpTimestamp: 0xE1E2E3E4_F1F2F3F4, RtpTimestamp: 90000,
            SenderPacketCount: 1234, SenderOctetCount: 567890, blocks);

        byte[] buffer = new byte[256];
        int length = sr.Write(buffer);

        Assert.IsTrue(RtcpSenderReport.TryParse(buffer.AsSpan(0, length), out RtcpSenderReport parsed));
        Assert.AreEqual(0x11112222u, parsed.Ssrc);
        Assert.AreEqual(0xE1E2E3E4_F1F2F3F4ul, parsed.NtpTimestamp);
        Assert.AreEqual(90000u, parsed.RtpTimestamp);
        Assert.AreEqual(1234u, parsed.SenderPacketCount);
        Assert.AreEqual(567890u, parsed.SenderOctetCount);
        Assert.AreEqual(1, parsed.ReportBlocks.Count);
        Assert.AreEqual(0xAABBCCDDu, parsed.ReportBlocks[0].Ssrc);
        Assert.AreEqual(100, parsed.ReportBlocks[0].CumulativeLost);
        Assert.AreEqual(5000u, parsed.ReportBlocks[0].ExtendedHighestSequence);
    }

    [TestMethod]
    public void ReceiverReport_WriteThenParse_RoundTrips()
    {
        var blocks = new RtcpReportBlock[]
        {
            new(0xDEADBEEF, 0, 0, 12345, 7, 0, 0),
        };
        var rr = new RtcpReceiverReport(0x99887766, blocks);

        byte[] buffer = new byte[128];
        int length = rr.Write(buffer);

        Assert.IsTrue(RtcpReceiverReport.TryParse(buffer.AsSpan(0, length), out RtcpReceiverReport parsed));
        Assert.AreEqual(0x99887766u, parsed.Ssrc);
        Assert.AreEqual(1, parsed.ReportBlocks.Count);
        Assert.AreEqual(0xDEADBEEFu, parsed.ReportBlocks[0].Ssrc);
        Assert.AreEqual(12345u, parsed.ReportBlocks[0].ExtendedHighestSequence);
    }

    [TestMethod]
    public void Compound_SenderReportFollowedByReceiverReport_BothParse()
    {
        var sr = new RtcpSenderReport(1, 0xAAAA_BBBB_CCCC_DDDD, 500, 10, 20, []);
        var rr = new RtcpReceiverReport(2, [new RtcpReportBlock(3, 0, 0, 99, 0, 0, 0)]);

        byte[] buffer = new byte[256];
        int n = sr.Write(buffer);
        n += rr.Write(buffer.AsSpan(n));

        Assert.IsTrue(RtcpSenderReport.TryParse(buffer.AsSpan(0, n), out RtcpSenderReport parsedSr));
        Assert.AreEqual(0xAAAA_BBBB_CCCC_DDDDul, parsedSr.NtpTimestamp);
        Assert.IsTrue(RtcpReceiverReport.TryParse(buffer.AsSpan(0, n), out RtcpReceiverReport parsedRr));
        Assert.AreEqual(2u, parsedRr.Ssrc);
        Assert.AreEqual(99u, parsedRr.ReportBlocks[0].ExtendedHighestSequence);
    }
}
