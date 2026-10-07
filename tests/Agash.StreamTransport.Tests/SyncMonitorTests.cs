using Agash.StreamTransport.Media;
using Agash.StreamTransport.Sync;
using Microsoft.Extensions.Time.Testing;

namespace Agash.StreamTransport.Tests;

[TestClass]
public sealed class SyncMonitorTests
{
    // NTP timestamps resolve 2^-32 s, so a round trip through one lands within a microsecond.
    private const double Microsecond = 0.001;

    // Audio captured at c plays at t; video captured at c + 50 ms shows at t + 10 ms, when the audio heard
    // was captured at c + 10 ms: the video is 40 ms ahead of the audio, so audio lags by 40 ms.
    [TestMethod]
    public void VideoAheadOfTheAudioHeard_MeasuresAudioLagging()
    {
        FakeTimeProvider time = new();
        using StreamTransportMetrics metrics = new(null);
        SyncMonitor monitor = new(new MediaClock(time), metrics);
        var capture = NtpTime.From(time.GetUtcNow());

        monitor.AudioPresented(capture);
        time.Advance(TimeSpan.FromMilliseconds(10));
        monitor.VideoPresented(capture + TimeSpan.FromMilliseconds(50));

        Assert.AreEqual(40, monitor.Offset!.Value.TotalMilliseconds, Microsecond);
    }

    [TestMethod]
    public void VideoBehindTheAudioHeard_MeasuresAudioLeading()
    {
        FakeTimeProvider time = new();
        using StreamTransportMetrics metrics = new(null);
        SyncMonitor monitor = new(new MediaClock(time), metrics);
        var capture = NtpTime.From(time.GetUtcNow());

        monitor.AudioPresented(capture + TimeSpan.FromMilliseconds(100));
        monitor.VideoPresented(capture);

        Assert.AreEqual(-100, monitor.Offset!.Value.TotalMilliseconds, Microsecond);
    }

    [TestMethod]
    public void NoRecentAudio_MeasuresNothing()
    {
        FakeTimeProvider time = new();
        using StreamTransportMetrics metrics = new(null);
        SyncMonitor monitor = new(new MediaClock(time), metrics);
        var capture = NtpTime.From(time.GetUtcNow());

        monitor.VideoPresented(capture);
        Assert.IsNull(monitor.Offset, "no audio yet");

        monitor.AudioPresented(capture);
        time.Advance(TimeSpan.FromSeconds(1));
        monitor.VideoPresented(capture + TimeSpan.FromSeconds(1));
        Assert.IsNull(monitor.Offset, "audio stopped half a second ago");
    }

    // The smoothed offset follows a steady offset and moves a sixteenth of the way to each new one.
    [TestMethod]
    public void Offset_IsSmoothedOverMeasurements()
    {
        FakeTimeProvider time = new();
        using StreamTransportMetrics metrics = new(null);
        SyncMonitor monitor = new(new MediaClock(time), metrics);
        var capture = NtpTime.From(time.GetUtcNow());

        monitor.AudioPresented(capture);
        monitor.VideoPresented(capture);
        monitor.VideoPresented(capture + TimeSpan.FromMilliseconds(160));

        Assert.AreEqual(10, monitor.Offset!.Value.TotalMilliseconds, Microsecond);
    }
}
