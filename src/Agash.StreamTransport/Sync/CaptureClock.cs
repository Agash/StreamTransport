using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Sync;

/// <summary>
/// Maps capture instants on the monotonic media clock to wall-clock NTP time, for abs-capture-time. One
/// anchor taken at creation serves every stream of a session, so audio and video captured at the same
/// instant carry the same NTP time and the receiver lip-syncs them by capture time.
/// </summary>
internal sealed class CaptureClock
{
    private readonly MediaTime _anchorMedia;
    private readonly NtpTime _anchorWall;

    /// <summary>A mapping anchored now.</summary>
    /// <param name="clock">The media clock capture times are on.</param>
    public CaptureClock(MediaClock clock)
    {
        _anchorMedia = clock.Now;
        _anchorWall = NtpTime.From(clock.TimeProvider.GetUtcNow());
    }

    /// <summary>The wall-clock time of a capture instant.</summary>
    /// <param name="capture">When the media was captured, on the media clock.</param>
    /// <returns>The NTP time.</returns>
    public NtpTime ToNtp(MediaTime capture) => _anchorWall + (capture - _anchorMedia);
}
