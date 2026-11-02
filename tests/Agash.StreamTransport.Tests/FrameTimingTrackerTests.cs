using Agash.StreamTransport.Media;
using Agash.StreamTransport.Streams;

namespace Agash.StreamTransport.Tests;

// Timing frame choice as libwebrtc's FrameEncodeMetadataWriter makes it: every 200 ms of capture time, and
// any frame five times the average size or more.
[TestClass]
public sealed class FrameTimingTrackerTests
{
    private static readonly MediaTime Start = MediaTime.FromTimeSpan(TimeSpan.FromSeconds(10));

    [TestMethod]
    public void FirstFrame_IsTimedByTheTimer_WithItsEncodeOffsets()
    {
        FrameTimingTracker tracker = new();
        tracker.EncodeStarted(Start, Start + TimeSpan.FromMilliseconds(4));

        FrameSendTiming? timing = tracker.EncodeFinished(
            Start,
            1000,
            1_000_000,
            30,
            Start + TimeSpan.FromMilliseconds(9)
        );

        Assert.AreEqual(
            new FrameSendTiming(
                TimeSpan.FromMilliseconds(4),
                TimeSpan.FromMilliseconds(9),
                FrameTimingReasons.Timer
            ),
            timing
        );
    }

    [TestMethod]
    public void FramesWithin200Milliseconds_AreNotTimed_TheNextOneIs()
    {
        FrameTimingTracker tracker = new();
        List<FrameTimingReasons> reasons = [];
        for (int i = 0; i <= 12; i++)
        {
            MediaTime capture = Start + TimeSpan.FromMilliseconds(i * 33.4);
            tracker.EncodeStarted(capture, capture);
            reasons.Add(
                tracker.EncodeFinished(capture, 1000, 1_000_000, 30, capture)?.Reasons
                    ?? FrameTimingReasons.None
            );
        }

        // 33.4 ms apart: frames 0, 6 (200.4 ms) and 12 are timed.
        CollectionAssert.AreEqual(new[] { 0, 6, 12 }, Timed(reasons));
    }

    // 1 Mbit/s at 25 fps averages 5000 bytes; five times that or more is an outlier.
    [TestMethod]
    public void LargeFrame_IsTimedForItsSize()
    {
        FrameTimingTracker tracker = new();
        tracker.EncodeStarted(Start, Start);
        _ = tracker.EncodeFinished(Start, 1000, 1_000_000, 25, Start);
        MediaTime next = Start + TimeSpan.FromMilliseconds(40);
        tracker.EncodeStarted(next, next);

        Assert.AreEqual(
            FrameTimingReasons.Size,
            tracker.EncodeFinished(next, 25_000, 1_000_000, 25, next)?.Reasons
        );
    }

    // A frame whose start was not recorded (a stream switched encoders mid-frame) has no timing.
    [TestMethod]
    public void FrameWithoutARecordedStart_IsNotTimed()
    {
        FrameTimingTracker tracker = new();

        Assert.IsNull(tracker.EncodeFinished(Start, 1000, 1_000_000, 30, Start));
    }

    private static int[] Timed(List<FrameTimingReasons> reasons) =>
        [.. Enumerable.Range(0, reasons.Count).Where(i => reasons[i] != FrameTimingReasons.None)];
}
