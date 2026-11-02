using Agash.StreamTransport.WebRtc.Rtcp;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class CcfbTests
{
    [TestMethod]
    public void MetricStore_KeepsEveryBlock_AndReusesItsMemoryAfterClear()
    {
        CcfbMetricStore store = new();
        Memory<CcfbMetric> first = store.Rent(1000);
        first.Span.Fill(new CcfbMetric(true, 1, 7));

        // More than the first chunk has left, and more than a chunk: neither moves the first block.
        Memory<CcfbMetric> second = store.Rent(100);
        Memory<CcfbMetric> large = store.Rent(5000);
        second.Span.Fill(new CcfbMetric(false, 0, 0));
        large.Span.Fill(new CcfbMetric(true, 3, 1));
        Assert.IsTrue(first.Span.ToArray().All(static m => m is { Received: true, Ecn: 1 }));
        Assert.AreEqual(5000, large.Length);

        store.Clear();
        Memory<CcfbMetric> again = store.Rent(1000);
        Assert.IsTrue(again.Span.Overlaps(first.Span), "a cleared store hands out the same memory");
        Assert.AreEqual(0, store.Rent(0).Length);
    }

    [TestMethod]
    public void BuildThenParse_RoundTripsMultipleStreamsWithLossAndEcn()
    {
        var stream1 = new CcfbStreamReport(
            0x1111_1111,
            BeginSequence: 1000,
            (CcfbMetric[])
                [
                    new CcfbMetric(Received: true, Ecn: 0, ArrivalTimeOffset: 100),
                    new CcfbMetric(Received: false, Ecn: 0, ArrivalTimeOffset: 0), // lost
                    new CcfbMetric(Received: true, Ecn: 3, ArrivalTimeOffset: 250), // ECN-CE
                ]
        );
        var stream2 = new CcfbStreamReport(
            0x2222_2222,
            BeginSequence: 5,
            (CcfbMetric[])
                [new CcfbMetric(Received: true, Ecn: 1, ArrivalTimeOffset: Ccfb.ArrivalTimeUnknown)]
        );

        byte[] buffer = new byte[256];
        int length = Ccfb.Build(
            buffer,
            senderSsrc: 0xFEED_BEEF,
            [stream1, stream2],
            reportTimestamp: 0xABCD_1234
        );

        var parsed = new List<CcfbStreamReport>();
        Assert.IsTrue(
            Ccfb.TryParse(
                buffer.AsSpan(0, length),
                out uint senderSsrc,
                out uint reportTimestamp,
                parsed,
                new CcfbMetricStore()
            )
        );

        Assert.AreEqual(0xFEED_BEEFu, senderSsrc);
        Assert.AreEqual(0xABCD_1234u, reportTimestamp);
        Assert.AreEqual(2, parsed.Count);

        Assert.AreEqual(0x1111_1111u, parsed[0].Ssrc);
        Assert.AreEqual(1000, parsed[0].BeginSequence);
        Assert.AreEqual(3, parsed[0].Metrics.ToArray().Length);
        Assert.IsTrue(parsed[0].Metrics.ToArray()[0].Received);
        Assert.AreEqual(100, parsed[0].Metrics.ToArray()[0].ArrivalTimeOffset);
        Assert.IsFalse(parsed[0].Metrics.ToArray()[1].Received);
        Assert.AreEqual(3, parsed[0].Metrics.ToArray()[2].Ecn);
        Assert.AreEqual(250, parsed[0].Metrics.ToArray()[2].ArrivalTimeOffset);

        Assert.AreEqual(0x2222_2222u, parsed[1].Ssrc);
        Assert.AreEqual(Ccfb.ArrivalTimeUnknown, parsed[1].Metrics.ToArray()[0].ArrivalTimeOffset);
        Assert.AreEqual(1, parsed[1].Metrics.ToArray()[0].Ecn);
    }

    [TestMethod]
    public void Build_PadsOddReportCountTo32BitBoundary()
    {
        // 1 metric (2 bytes) must be padded to 4; total = 8 (hdr) + 8 (stream hdr) + 4 (metric+pad) + 4 (RTS) = 24.
        var stream = new CcfbStreamReport(1, 0, (CcfbMetric[])[new CcfbMetric(true, 0, 1)]);
        byte[] buffer = new byte[64];
        int length = Ccfb.Build(buffer, 1, [stream], 0);
        Assert.AreEqual(0, length % 4);
        Assert.AreEqual(24, length);
    }

    [TestMethod]
    public void TryParse_NonCcfbRtcp_ReturnsFalse()
    {
        // A well-formed RTCP packet that is not CCFB (PT 200 Sender Report) must not parse as CCFB.
        byte[] sr = new byte[8];
        sr[0] = 0x80;
        sr[1] = 200; // SR
        sr[2] = 0x00;
        sr[3] = 0x01;

        Assert.IsFalse(Ccfb.TryParse(sr, out _, out _, [], new CcfbMetricStore()));
    }

    [TestMethod]
    public void TryParse_TruncatedPacket_DoesNotThrow_AndReturnsFalse()
    {
        // Truncated/garbage input must be rejected without throwing (hostile-network robustness).
        Assert.IsFalse(Ccfb.TryParse([], out _, out _, [], new CcfbMetricStore()));
        Assert.IsFalse(Ccfb.TryParse([0x80, 205, 0x00], out _, out _, [], new CcfbMetricStore()));
    }

    [TestMethod]
    public void RoundTrip_PreservesArrivalTimeUnknownSentinel_AndAllEcnCodepoints()
    {
        // The 0x1FFF "arrival time unknown" sentinel and every 2-bit ECN codepoint must survive a round trip.
        var stream = new CcfbStreamReport(
            0x9999_9999,
            BeginSequence: 7,
            (CcfbMetric[])
                [
                    new CcfbMetric(
                        Received: true,
                        Ecn: 0,
                        ArrivalTimeOffset: Ccfb.ArrivalTimeUnknown
                    ),
                    new CcfbMetric(Received: true, Ecn: 1, ArrivalTimeOffset: 0), // ECT(1)
                    new CcfbMetric(Received: true, Ecn: 2, ArrivalTimeOffset: 8190), // ECT(0)
                    new CcfbMetric(Received: true, Ecn: 3, ArrivalTimeOffset: 1), // CE
                ]
        );

        byte[] buffer = new byte[128];
        int length = Ccfb.Build(buffer, 0x1, [stream], 0x55);
        var parsed = new List<CcfbStreamReport>();
        Assert.IsTrue(
            Ccfb.TryParse(buffer.AsSpan(0, length), out _, out _, parsed, new CcfbMetricStore())
        );

        IReadOnlyList<CcfbMetric> m = parsed[0].Metrics.ToArray();
        Assert.AreEqual(Ccfb.ArrivalTimeUnknown, m[0].ArrivalTimeOffset);
        Assert.AreEqual(1, m[1].Ecn);
        Assert.AreEqual(2, m[2].Ecn);
        Assert.AreEqual(8190, m[2].ArrivalTimeOffset);
        Assert.AreEqual(3, m[3].Ecn);
    }
}
