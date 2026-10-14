using Agash.StreamTransport.WebRtc.Rtcp;

namespace Agash.StreamTransport.WebRtc.Tests;

// RFC 8083 section 4.1: which RTCP keeps the timeout breaker from tripping.
[TestClass]
public sealed class RtcpTimeoutFeedbackTests
{
    private const uint Ours = 0x1111;
    private const uint OtherOfOurs = 0x2222;
    private const uint Theirs = 0x9999;
    private const uint Peer = 0x5555;

    private static bool IsOurs(uint ssrc) => ssrc is Ours or OtherOfOurs;

    [TestMethod]
    public void Resets_AReceiverReportAboutOurStream_IsFeedback()
    {
        Assert.IsTrue(RtcpTimeoutFeedback.Resets(ReceiverReport(Ours), IsOurs));
    }

    // Every stream shares the 5-tuple, so a report about any of them shows the return path works.
    [TestMethod]
    public void Resets_AReportAboutAnotherOfOurStreams_IsFeedback()
    {
        Assert.IsTrue(RtcpTimeoutFeedback.Resets(SenderReport(OtherOfOurs), IsOurs));
    }

    // The gap this closes: a receiver that keeps reporting, but on other sources only, is not
    // hearing this endpoint's media.
    [TestMethod]
    public void Resets_ReportsAboutOtherSourcesOnly_AreNotFeedback()
    {
        Assert.IsFalse(RtcpTimeoutFeedback.Resets(ReceiverReport(Theirs), IsOurs));
        Assert.IsFalse(RtcpTimeoutFeedback.Resets(SenderReport(), IsOurs));
    }

    [TestMethod]
    public void Resets_ReducedSizeRtcp_IsFeedback()
    {
        byte[] pli = new byte[12];
        int length = RtcpFeedback.BuildPli(pli, Peer, Ours);

        Assert.IsTrue(RtcpTimeoutFeedback.Resets(pli.AsSpan(0, length), IsOurs));
    }

    // A compound packet starts with its report; feedback inside it does not make it reduced-size.
    [TestMethod]
    public void Resets_FeedbackAfterAReportOnOtherSources_IsNotFeedback()
    {
        byte[] report = ReceiverReport(Theirs);
        byte[] pli = new byte[12];
        int pliLength = RtcpFeedback.BuildPli(pli, Peer, Ours);

        Assert.IsFalse(
            RtcpTimeoutFeedback.Resets([.. report, .. pli.AsSpan(0, pliLength)], IsOurs)
        );
    }

    private static RtcpReportBlock Block(uint ssrc) => new(ssrc, 0, 0, 100, 0, 0, 0);

    private static byte[] ReceiverReport(uint about)
    {
        byte[] buffer = new byte[64];
        int length = new RtcpReceiverReport(Peer, [Block(about)]).Write(buffer);
        return buffer[..length];
    }

    private static byte[] SenderReport(params uint[] about)
    {
        byte[] buffer = new byte[128];
        int length = new RtcpSenderReport(Peer, 0, 0, 0, 0, [.. about.Select(Block)]).Write(buffer);
        return buffer[..length];
    }
}
