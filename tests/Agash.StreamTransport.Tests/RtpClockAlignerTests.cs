using Agash.StreamTransport.Sync;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// Pure-math tests for <see cref="RtpClockAligner"/> (runs in CI - no WebRTC). Proves that audio and video
/// RTP timestamps map onto the sender's single NTP wall clock through their own Sender Report anchors, so the
/// two streams become directly comparable, and that 32-bit RTP timestamp wraparound is handled.
/// </summary>
[TestClass]
public sealed class RtpClockAlignerTests
{
    private const int VideoClock = 90_000;
    private const int AudioClock = 48_000;

    // 64-bit NTP: high 32 bits = seconds since 1900, low 32 bits = fraction. Build one for a whole second.
    private static ulong Ntp(uint seconds, double fraction = 0) =>
        ((ulong)seconds << 32) | (uint)(fraction * 4_294_967_296d);

    [TestMethod]
    public void NotAligned_UntilBothStreamsHaveASenderReport()
    {
        var aligner = new RtpClockAligner();
        Assert.IsFalse(aligner.BothAligned);

        aligner.RecordSenderReport(SyncStream.Video, Ntp(1000), 0, VideoClock);
        Assert.IsFalse(aligner.BothAligned, "one stream's SR is not enough for cross-stream alignment.");

        aligner.RecordSenderReport(SyncStream.Audio, Ntp(1000), 0, AudioClock);
        Assert.IsTrue(aligner.BothAligned);
    }

    [TestMethod]
    public void TryToSenderWallNs_ReturnsFalseBeforeStreamSenderReport()
    {
        var aligner = new RtpClockAligner();
        Assert.IsFalse(aligner.TryToSenderWallNs(SyncStream.Video, 12345, out _));
    }

    [TestMethod]
    public void MapsRtpTimestampToWallClock_OneSecondPastAnchor()
    {
        var aligner = new RtpClockAligner();
        // Anchor: at NTP second 1000, the video RTP timestamp was 90000.
        aligner.RecordSenderReport(SyncStream.Video, Ntp(1000), 90_000, VideoClock);

        // One video clock-second later (RTP 90000 + 90000) must be exactly one second past the anchor wall time.
        Assert.IsTrue(aligner.TryToSenderWallNs(SyncStream.Video, 180_000, out long wallNs));
        long anchorNs = 1000L * 1_000_000_000L;
        Assert.AreEqual(anchorNs + 1_000_000_000L, wallNs);
    }

    [TestMethod]
    public void AudioAndVideo_LandOnTheSameTimeline_ForACorrelatedCaptureInstant()
    {
        var aligner = new RtpClockAligner();
        // Both streams' SRs come from the sender's one NTP clock at the same wall instant (NTP second 5000),
        // but with each stream's own (independent, random) RTP base and clock rate.
        aligner.RecordSenderReport(SyncStream.Video, Ntp(5000), 1_000_000, VideoClock);
        aligner.RecordSenderReport(SyncStream.Audio, Ntp(5000), 7_777_000, AudioClock);

        // A frame captured 0.5 s after the SR instant on each stream: video advances 45000 ticks, audio 24000.
        Assert.IsTrue(aligner.TryToSenderWallNs(SyncStream.Video, 1_000_000 + 45_000, out long videoWall));
        Assert.IsTrue(aligner.TryToSenderWallNs(SyncStream.Audio, 7_777_000 + 24_000, out long audioWall));

        // The two map to the same sender wall time (within 1 ns) - that is what makes them comparable.
        Assert.AreEqual(videoWall, audioWall, "correlated capture instants must align on one wall clock.");
        Assert.AreEqual((5000L * 1_000_000_000L) + 500_000_000L, videoWall);
    }

    [TestMethod]
    public void Handles32BitRtpTimestampWraparound()
    {
        var aligner = new RtpClockAligner();
        // Anchor near the top of the uint range so the next frame wraps past 0.
        uint anchorRtp = uint.MaxValue - 9_000; // 0.1 s of 90 kHz before wrap.
        aligner.RecordSenderReport(SyncStream.Video, Ntp(2000), anchorRtp, VideoClock);

        // 18000 ticks (0.2 s) later wraps the 32-bit counter; the signed delta must still give +0.2 s.
        uint wrapped = unchecked(anchorRtp + 18_000);
        Assert.IsTrue(aligner.TryToSenderWallNs(SyncStream.Video, wrapped, out long wallNs));
        Assert.AreEqual((2000L * 1_000_000_000L) + 200_000_000L, wallNs);
    }
}
