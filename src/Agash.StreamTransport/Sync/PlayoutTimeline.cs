namespace Agash.StreamTransport.Sync;

/// <summary>
/// The pure scheduling math behind the playout scheduler, separated from threading so it is
/// deterministically unit-testable. It maps a frame's <b>sender wall-clock capture time</b> (from
/// <see cref="RtpClockAligner"/>) to a <b>local release time</b>: <c>release = senderWall + clockOffset +
/// targetDelay</c>.
///
/// <para><b>clockOffset</b> estimates (local clock − sender clock) as a <i>leaky minimum</i> of the observed
/// <c>(localNow − senderWall)</c> over every scheduled frame of <i>both</i> streams (the libwebrtc
/// <c>RemoteNtpTimeEstimator</c> approach). The minimum is the lowest-latency path - the best estimate of the
/// true offset - and taking it across both streams keeps audio and video on one shared offset so frames
/// captured at the same instant release together (lip-sync). A pure minimum is robust to what a UDP/realtime
/// transport actually does - cold-start transients, jitter spikes, dropped frames never lower it - so no
/// single or missing frame can skew the session. A slow upward leak follows real inter-machine clock drift.</para>
///
/// <para><b>targetDelay</b> is the jitter-buffer depth, and it <i>adapts</i> rather than being fixed (the
/// libwebrtc <c>JitterEstimator</c> idea). Each frame's delay above the best path - <c>(localNow − senderWall)
/// − clockOffset</c> - is the network jitter for that frame; the buffer is sized to a <i>leaky maximum</i> of
/// that jitter (rises to spikes at once, decays slowly as the link calms) plus a decode/render margin, clamped
/// to [min, max]. So on a clean link the delay shrinks toward the floor (low latency) and on a jittery
/// cellular link it grows to avoid underruns. Because the depth covers the worst (video) stream's jitter, the
/// slower stream still makes its slot; an arrived-late frame presents immediately and never corrupts the
/// estimate, so timing recovers on its own.</para>
/// </summary>
internal sealed class PlayoutTimeline
{
    // Maximum upward drift of the offset estimate, as a fraction of elapsed time (~200 ppm) - above real
    // inter-machine crystal drift yet far below per-frame jitter, so it tracks drift without chasing noise.
    private const double MaxDriftPpm = 200.0;

    // How fast the jitter estimate decays when no new spike arrives, in ns of estimate shed per second. ~40 ms/s
    // lets a transient spike bleed off over a few seconds so the buffer (and latency) returns to the link floor.
    private const long JitterDecayNsPerSec = 40_000_000;

    private readonly long _minDelayNs;
    private readonly long _maxDelayNs;
    private readonly long _marginNs;

    private long _jitterNs;
    private long _lastUpdateLocalNs;

    /// <summary>True once at least one frame has established the estimates.</summary>
    public bool IsAnchored { get; private set; }

    /// <summary>The current local↔sender clock-offset estimate (local − sender), in nanoseconds.</summary>
    public long ClockOffsetNs { get; private set; }

    /// <param name="minDelayNs">Floor on the buffer depth (decode + render headroom), e.g. 40 ms.</param>
    /// <param name="maxDelayNs">Cap on the buffer depth, bounding worst-case latency, e.g. 500 ms.</param>
    /// <param name="marginNs">Headroom added above the measured jitter, e.g. 20 ms.</param>
    public PlayoutTimeline(long minDelayNs, long maxDelayNs, long marginNs)
    {
        _minDelayNs = minDelayNs;
        _maxDelayNs = maxDelayNs;
        _marginNs = marginNs;
    }

    /// <summary>The current adaptive target buffer depth, in nanoseconds.</summary>
    public long CurrentDelayNs => Math.Clamp(_jitterNs + _marginNs, _minDelayNs, _maxDelayNs);

    /// <summary>
    /// Compute the local release time for a frame captured at <paramref name="senderWallNs"/>, given the local
    /// clock reading <paramref name="localNowNs"/> at enqueue, updating the clock-offset and jitter estimates.
    /// </summary>
    public long ReleaseLocalNs(long senderWallNs, long localNowNs)
    {
        long raw = localNowNs - senderWallNs;
        if (!IsAnchored)
        {
            ClockOffsetNs = raw;
            _jitterNs = 0;
            _lastUpdateLocalNs = localNowNs;
            IsAnchored = true;
        }
        else
        {
            long elapsed = localNowNs - _lastUpdateLocalNs;
            if (elapsed < 0)
            {
                elapsed = 0;
            }

            // Leaky minimum offset: let it rise by at most the drift budget, then take the min with this frame.
            long offsetLeak = (long)(elapsed * (MaxDriftPpm / 1_000_000.0));
            ClockOffsetNs = Math.Min(raw, ClockOffsetNs + offsetLeak);

            // Leaky maximum jitter: this frame's delay above the best path, decayed slowly toward the floor.
            long excess = raw - ClockOffsetNs; // >= 0 by construction
            long jitterDecay = elapsed * JitterDecayNsPerSec / 1_000_000_000;
            _jitterNs = Math.Max(excess, _jitterNs - jitterDecay);

            _lastUpdateLocalNs = localNowNs;
        }

        return senderWallNs + ClockOffsetNs + Math.Clamp(_jitterNs + _marginNs, _minDelayNs, _maxDelayNs);
    }
}
