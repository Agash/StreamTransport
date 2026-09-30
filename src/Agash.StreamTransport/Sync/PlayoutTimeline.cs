using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Sync;

/// <summary>
/// The scheduling arithmetic of synced playout, apart from threading so tests drive it directly. A frame
/// captured at a sender instant is released at <c>capture + clock offset + target delay</c> on the local
/// media clock.
/// </summary>
/// <remarks>
/// <para>The clock offset (local minus sender) is a leaky minimum of <c>arrival - capture</c> over the
/// frames of both streams, as libwebrtc's remote NTP time estimator keeps it. The minimum is the
/// fastest path and the best estimate of the true offset; taking it across both streams keeps audio and
/// video on one offset, so frames captured together release together. Jitter spikes and lost frames
/// never lower it, and a slow upward leak follows real drift between the machines' clocks.</para>
/// <para>The target delay is the playout buffer depth, sized to a leaky maximum of each frame's delay
/// above the fastest path: it rises to a spike at once and decays as the link calms, plus a margin,
/// clamped between the configured bounds. On a clean link it shrinks to the floor; on a jittery link it
/// grows to avoid underruns. A frame that arrives late plays at once and does not disturb the
/// estimates.</para>
/// </remarks>
internal sealed class PlayoutTimeline
{
    // How far the offset may drift upward, as a fraction of elapsed time: above real crystal drift between
    // machines, far below per-frame jitter.
    private const double MaxDriftPerSecond = 200e-6;

    // How fast the jitter estimate decays without a new spike, in nanoseconds per second: a transient
    // bleeds off over a few seconds and the buffer returns to the link's floor.
    private const long JitterDecayPerSecond = 40_000_000;

    private readonly long _minDelay;
    private readonly long _maxDelay;
    private readonly long _margin;
    private long _offset;
    private long _jitter;
    private MediaTime _lastUpdate;

    /// <summary>A timeline with buffer bounds.</summary>
    /// <param name="minDelay">The shortest buffer: decode and render headroom.</param>
    /// <param name="maxDelay">The longest buffer, bounding latency.</param>
    /// <param name="margin">Headroom above the measured jitter.</param>
    public PlayoutTimeline(TimeSpan minDelay, TimeSpan maxDelay, TimeSpan margin)
    {
        _minDelay = MediaTime.FromTimeSpan(minDelay).Nanoseconds;
        _maxDelay = MediaTime.FromTimeSpan(maxDelay).Nanoseconds;
        _margin = MediaTime.FromTimeSpan(margin).Nanoseconds;
    }

    /// <summary>Whether a frame has set the estimates.</summary>
    public bool IsAnchored { get; private set; }

    /// <summary>The playout buffer depth now.</summary>
    public TimeSpan CurrentDelay => new MediaTime(Delay).ToTimeSpan();

    private long Delay => Math.Clamp(_jitter + _margin, _minDelay, _maxDelay);

    /// <summary>
    /// When to release a frame captured at a sender instant, given the local time it is scheduled,
    /// updating the offset and jitter estimates.
    /// </summary>
    /// <param name="capture">The sender's capture instant.</param>
    /// <param name="now">The local media time now.</param>
    /// <returns>The local release time.</returns>
    public MediaTime Release(NtpTime capture, MediaTime now)
    {
        long raw = now.Nanoseconds - capture.Nanoseconds;
        if (!IsAnchored)
        {
            _offset = raw;
            _jitter = 0;
            IsAnchored = true;
        }
        else
        {
            long elapsed = Math.Max(0, (now - _lastUpdate).Ticks * 100);
            _offset = Math.Min(raw, _offset + (long)(elapsed * MaxDriftPerSecond));
            long decay = elapsed * JitterDecayPerSecond / 1_000_000_000;
            _jitter = Math.Max(raw - _offset, _jitter - decay);
        }

        _lastUpdate = now;
        return new MediaTime(capture.Nanoseconds + _offset + Delay);
    }
}
