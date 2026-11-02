using Agash.StreamTransport.Media;
using Agash.StreamTransport.WebRtc.Transport;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class RtpClockAlignerTests
{
    [TestMethod]
    public void Aligner_MapsTimestampsAroundItsAnchorAndAcrossTheWrap()
    {
        RtpClockAligner aligner = new(new ClockRate(90_000));
        Assert.IsFalse(aligner.TryGetCapture(0, out _));

        var anchor = NtpTime.FromNanoseconds(3_900_000_000_000_000_000);
        aligner.Record(anchor, uint.MaxValue - 44_999);

        Assert.IsTrue(aligner.TryGetCapture(45_000, out NtpTime later));
        Assert.AreEqual(1_000_000_000, later.Nanoseconds - anchor.Nanoseconds, 1_000);
        Assert.IsTrue(aligner.TryGetCapture(uint.MaxValue - 89_999, out NtpTime earlier));
        Assert.AreEqual(-500_000_000, earlier.Nanoseconds - anchor.Nanoseconds, 1_000);
    }
}
