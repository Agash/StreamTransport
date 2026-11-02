using Agash.StreamTransport.WebRtc.Rtcp;

namespace Agash.StreamTransport.WebRtc.Tests;

// RFC 3550 sections 6.2 and 6.3 and appendix A.7, RFC 3556, RFC 4585 section 3.5, RFC 8108 section 5.
[TestClass]
public sealed class RtcpSchedulerTests
{
    private static readonly RtcpMembership TwoPeers = new(2, 1, true);

    // RFC 3556 section 3: without RS or RR, RTCP is 5% of the session and a quarter of it the senders'.
    [TestMethod]
    public void Bandwidth_WithoutRsOrRr_IsFivePercentAQuarterForSenders()
    {
        var bandwidth = RtcpBandwidth.From(1_000_000, null, null);

        Assert.AreEqual(50_000 / 8.0, bandwidth.BytesPerSecond, 1e-9);
        Assert.AreEqual(0.25, bandwidth.SenderFraction, 1e-9);
    }

    // Section 2: RS and RR together are the RTCP bandwidth; one alone takes from the 5%.
    [TestMethod]
    public void Bandwidth_FromRsAndRr_FollowsRfc3556()
    {
        var both = RtcpBandwidth.From(1_000_000, 8_000, 24_000);
        Assert.AreEqual(32_000 / 8.0, both.BytesPerSecond, 1e-9);
        Assert.AreEqual(0.25, both.SenderFraction, 1e-9);

        var receiversOnly = RtcpBandwidth.From(1_000_000, null, 40_000);
        Assert.AreEqual(50_000 / 8.0, receiversOnly.BytesPerSecond, 1e-9);
        Assert.AreEqual(0.2, receiversOnly.SenderFraction, 1e-9);
    }

    // RFC 4585 section 3.5.1: between two peers the first report is not delayed.
    [TestMethod]
    public void Start_BetweenTwoPeers_IsDueAtOnce()
    {
        RtcpScheduler scheduler = new(TimeSpan.Zero, () => 0.5);

        scheduler.Start(1_000, TwoPeers, RtcpBandwidth.From(64_000, null, null));

        Assert.AreEqual(1_000, scheduler.Next);
        Assert.IsTrue(scheduler.Expire(1_000, TwoPeers, RtcpBandwidth.From(64_000, null, null)));
    }

    // Appendix A.7: the interval is avg_rtcp_size * n / rtcp_bw, randomized and divided by e - 3/2; with
    // the random draw at its middle the randomization is 1.
    [TestMethod]
    public void Sent_SchedulesTheAppendixA7Interval()
    {
        RtcpScheduler scheduler = new(TimeSpan.Zero, () => 0.5);
        var bandwidth = RtcpBandwidth.From(64_000, null, null);
        scheduler.Start(0, TwoPeers, bandwidth);

        scheduler.Sent(0, TwoPeers, bandwidth);

        // One sender of two is more than a quarter, so n is every member: 100 B * 2 / 400 B/s = 0.5 s.
        double expected = 0.5 / (Math.E - 1.5) * 1e6;
        Assert.AreEqual(expected, scheduler.Next, 1);
    }

    // Section 6.2: the interval grows with the members, so the session's RTCP stays within its share.
    [TestMethod]
    public void Sent_MoreMembers_ReportLessOften()
    {
        var bandwidth = RtcpBandwidth.From(64_000, null, null);
        RtcpScheduler two = new(TimeSpan.Zero, () => 0.5);
        RtcpScheduler twenty = new(TimeSpan.Zero, () => 0.5);

        two.Sent(0, new RtcpMembership(2, 0, false), bandwidth);
        twenty.Sent(0, new RtcpMembership(20, 0, false), bandwidth);

        Assert.AreEqual(10, twenty.Next / (double)two.Next, 0.01);
    }

    // Section 6.3.6: a timer expiring before last + interval is reconsidered, not sent.
    [TestMethod]
    public void Expire_BeforeTheReconsideredTime_DoesNotSend()
    {
        double draw = 0.5;
        RtcpScheduler scheduler = new(TimeSpan.Zero, () => draw);
        var bandwidth = RtcpBandwidth.From(64_000, null, null);
        scheduler.Sent(0, TwoPeers, bandwidth);
        long due = scheduler.Next;

        // The membership grew: the reconsidered interval is longer than the one scheduled.
        RtcpMembership grown = new(8, 1, true);
        Assert.IsFalse(scheduler.Expire(due, grown, bandwidth));
        Assert.IsGreaterThan(due, scheduler.Next);
        Assert.IsTrue(scheduler.Expire(scheduler.Next, grown, bandwidth));
    }

    // RFC 4585 section 3.5.3: regular reports keep at least a dithered T_rr_interval apart however much
    // bandwidth RTCP has.
    [TestMethod]
    public void Sent_WithARegularInterval_WaitsAtLeastHalfOfIt()
    {
        RtcpScheduler scheduler = new(TimeSpan.FromSeconds(1), () => 0.0);
        var plenty = RtcpBandwidth.From(100_000_000, null, null);

        scheduler.Sent(0, TwoPeers, plenty);

        Assert.AreEqual(500_000, scheduler.Next);
    }

    // Section 6.3.4: members leaving pull the next report toward now in proportion.
    [TestMethod]
    public void MembersLeft_PullsTheNextReportCloser()
    {
        RtcpScheduler scheduler = new(TimeSpan.Zero, () => 0.5);
        var bandwidth = RtcpBandwidth.From(64_000, null, null);
        scheduler.Sent(0, new RtcpMembership(10, 1, true), bandwidth);
        long before = scheduler.Next;

        scheduler.MembersLeft(0, 5, 10);

        Assert.AreEqual(before / 2.0, scheduler.Next, 1);
    }

    // Section 6.3.5 as RFC 8108 section 7.1.4 updates it: five deterministic intervals at the fixed 5 s
    // minimum, with or without T_rr_interval.
    [TestMethod]
    [DataRow(1)]
    [DataRow(0)]
    public void MemberTimeout_UsesTheFixedMinimum(int regularSeconds)
    {
        RtcpScheduler scheduler = new(TimeSpan.FromSeconds(regularSeconds));

        Assert.AreEqual(
            25_000_000,
            scheduler.MemberTimeout(TwoPeers, RtcpBandwidth.From(100_000_000, null, null))
        );
    }

    // RFC 8108 section 5.3.1: a compound packet from several sources counts its size per source.
    [TestMethod]
    public void Count_DividesByTheReportingSources()
    {
        RtcpScheduler scheduler = new(TimeSpan.Zero);

        scheduler.Count(300, reportingSources: 3);

        Assert.AreEqual(100, scheduler.AverageSize, 1e-9);
        scheduler.Count(260, reportingSources: 1);
        Assert.AreEqual(110, scheduler.AverageSize, 1e-9);
    }
}
