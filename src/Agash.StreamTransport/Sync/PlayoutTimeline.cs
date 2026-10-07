using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Sync;

/// <summary>Which of a session's streams a frame belongs to, for playout.</summary>
internal enum PlayoutStream
{
    Audio,
    Video,
}

/// <summary>
/// The scheduling arithmetic of synced playout, apart from threading so tests drive it directly. A frame
/// captured at a sender instant is released at <c>capture + offset + delay</c> on the local media clock.
/// </summary>
/// <remarks>
/// <para>Each stream keeps its own base delay: a leaky minimum of <c>arrival - capture</c>, its fastest path,
/// as libwebrtc's remote NTP time estimator keeps it, rising only as fast as real drift between the machines'
/// clocks. The offset is the smaller base, so the faster stream (usually audio, which skips the video
/// encoder) sets the timeline, and the other stream's base above it is its systematic path delay: what
/// lip sync must hold the faster stream back by, which is not jitter.</para>
/// <para>Each stream's jitter is the 95th percentile of its delay above its base over the last two seconds.
/// The first 30 frames of a stream are its startup, when encoders open and keyframes burst, and do not
/// count; samples more than 15 deviations from the median are clamped to that bound, as libwebrtc's jitter
/// estimator rejects delay outliers. The target delay covers the slower stream: the largest of each
/// stream's base above the offset plus its jitter, plus a margin, within the configured bounds.</para>
/// <para>A stream in its startup does not size the buffer: its first frames play on arrival when late,
/// and only once it is past its startup does it count toward the target. Its base still settles meanwhile,
/// so the encoder's slow first frames do not leave the buffer long.</para>
/// <para>The delay moves toward the target by at most 100 ms each second, as libwebrtc's video timing does
/// (larger steps show as freezes; smaller ones as a brief slow or fast motion). A frame that arrives after
/// its slot raises the delay at once by how late it is, up to the target, and plays immediately.</para>
/// </remarks>
internal sealed class PlayoutTimeline
{
    // How far a base may drift upward, as a fraction of elapsed time: above real crystal drift between
    // machines, far below per-frame jitter.
    private const double MaxDriftPerSecond = 200e-6;

    private const long MaxChangePerSecond = 100_000_000;

    private readonly long _minDelay;
    private readonly long _maxDelay;
    private readonly long _margin;
    private readonly StreamState[] _streams = [new(), new()];
    private long _delay;
    private bool _delaySet;
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
    public bool IsAnchored => _streams[0].Seen || _streams[1].Seen;

    /// <summary>The playout buffer depth now.</summary>
    public TimeSpan CurrentDelay => new MediaTime(_delay).ToTimeSpan();

    /// <summary>The delay the buffer is moving toward.</summary>
    public TimeSpan TargetDelay => new MediaTime(Target()).ToTimeSpan();

    /// <summary>
    /// When to release a frame captured at a sender instant, given the local time it is scheduled, updating
    /// the estimates.
    /// </summary>
    /// <param name="stream">The frame's stream.</param>
    /// <param name="capture">The sender's capture instant.</param>
    /// <param name="now">The local media time now.</param>
    /// <returns>The local release time.</returns>
    public MediaTime Release(PlayoutStream stream, NtpTime capture, MediaTime now)
    {
        long raw = now.Nanoseconds - capture.Nanoseconds;
        long elapsed = IsAnchored ? Math.Max(0, (now - _lastUpdate).Ticks * 100) : 0;
        _streams[(int)stream].Observe(raw, now, elapsed);
        _lastUpdate = now;

        long target = Target();
        if (!_delaySet)
        {
            _delay = target;
            _delaySet = true;
        }
        else
        {
            long step = Math.Max(1, elapsed * MaxChangePerSecond / 1_000_000_000);
            _delay += Math.Clamp(target - _delay, -step, step);
        }

        long offset = Offset();
        long release = capture.Nanoseconds + offset + _delay;
        if (release < now.Nanoseconds && _delay < target && _streams[(int)stream].Started)
        {
            _delay = Math.Min(target, _delay + (now.Nanoseconds - release));
        }

        return new MediaTime(release);
    }

    private long Offset() =>
        _streams[0].Seen && _streams[1].Seen ? Math.Min(_streams[0].Base, _streams[1].Base)
        : _streams[0].Seen ? _streams[0].Base
        : _streams[1].Base;

    // A stream in its startup sets no target yet: its first frames are late while its encoder opens, and
    // its base is not settled until faster frames arrive.
    private long Target()
    {
        long offset = Offset();
        long need = 0;
        foreach (StreamState stream in _streams)
        {
            if (stream.Started)
            {
                need = Math.Max(need, stream.Base - offset + stream.Jitter);
            }
        }

        return Math.Clamp(need + _margin, _minDelay, _maxDelay);
    }

    // One stream's delays: its base and the recent delays above it.
    private sealed class StreamState
    {
        private const int StartupSamples = 30;
        private const double OutlierDeviations = 15;
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

        private readonly Queue<(MediaTime At, long Raw)> _recent = new();
        private int _samples;
        private long? _jitter;

        public bool Seen { get; private set; }

        // Past its startup: its base and jitter are settled enough to size the buffer.
        public bool Started => _samples > StartupSamples;

        public long Base { get; private set; }

        public long Jitter => _jitter ??= Compute();

        public void Observe(long raw, MediaTime now, long elapsed)
        {
            Base = Seen ? Math.Min(raw, Base + (long)(elapsed * MaxDriftPerSecond)) : raw;
            Seen = true;
            _jitter = null;
            if (++_samples <= StartupSamples)
            {
                return;
            }

            _recent.Enqueue((now, raw));
            while (_recent.TryPeek(out (MediaTime At, long) oldest) && now - oldest.At > Window)
            {
                _ = _recent.Dequeue();
            }
        }

        // The 95th percentile of the window's delays above the base, outliers clamped.
        private long Compute()
        {
            if (_recent.Count == 0)
            {
                return 0;
            }

            long[] excess = new long[_recent.Count];
            int i = 0;
            foreach ((_, long raw) in _recent)
            {
                excess[i++] = Math.Max(0, raw - Base);
            }

            Array.Sort(excess);
            long median = excess[excess.Length / 2];
            long[] deviations = new long[excess.Length];
            for (int j = 0; j < excess.Length; j++)
            {
                deviations[j] = Math.Abs(excess[j] - median);
            }

            Array.Sort(deviations);

            // The median absolute deviation, at least a millisecond so a steady stream is not all outliers.
            long bound =
                median
                + (long)(
                    OutlierDeviations * Math.Max(deviations[deviations.Length / 2], 1_000_000)
                );
            for (int j = 0; j < excess.Length; j++)
            {
                excess[j] = Math.Min(excess[j], bound);
            }

            Array.Sort(excess);
            return excess[(int)Math.Ceiling(0.95 * excess.Length) - 1];
        }
    }
}
