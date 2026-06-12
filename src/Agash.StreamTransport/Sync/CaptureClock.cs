using System.Diagnostics;

namespace Agash.StreamTransport.Sync;

/// <summary>
/// Maps a frame's monotonic capture timestamp (a <see cref="Stopwatch"/>-based <c>PresentationTimeNs</c> on
/// the video/audio frame) onto an absolute NTP wall-clock time, for the <c>abs-capture-time</c> RTP header
/// extension. A single anchor
/// (wall ↔ monotonic) is taken once and reused, so the mapping is drift-free within a session and -
/// crucially - identical for audio and video, so frames captured at the same instant get the same NTP and
/// the receiver can lip-sync them off the capture clock rather than RTCP Sender Report timing.
/// </summary>
internal sealed class CaptureClock
{
    // 1 second is 2^32 units in a 64-bit NTP fixed-point timestamp (high 32 bits seconds, low 32 fraction).
    private const double NtpUnitsPerSecond = 4294967296.0;
    private static readonly DateTime s_ntpEpoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ulong _anchorNtp;
    private readonly long _anchorMonotonicNs;

    public CaptureClock()
    {
        _anchorMonotonicNs = NowMonotonicNs();
        _anchorNtp = ToNtp(DateTime.UtcNow);
    }

    /// <summary>
    /// The absolute capture time, as a 64-bit UQ32.32 NTP timestamp, for a frame whose monotonic capture
    /// timestamp is <paramref name="presentationTimeNs"/>.
    /// </summary>
    public ulong CaptureNtp(long presentationTimeNs)
    {
        long deltaNs = presentationTimeNs - _anchorMonotonicNs;
        long deltaNtpUnits = (long)(deltaNs * NtpUnitsPerSecond / 1_000_000_000.0);
        return unchecked(_anchorNtp + (ulong)deltaNtpUnits);
    }

    private static ulong ToNtp(DateTime utc)
    {
        double seconds = (utc - s_ntpEpoch).TotalSeconds;
        ulong whole = (ulong)Math.Floor(seconds);
        ulong fraction = (ulong)((seconds - whole) * NtpUnitsPerSecond);
        return (whole << 32) | (fraction & 0xFFFFFFFF);
    }

    private static long NowMonotonicNs() => Stopwatch.GetTimestamp() * (1_000_000_000L / Stopwatch.Frequency);
}
