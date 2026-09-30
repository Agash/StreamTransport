namespace Agash.StreamTransport.Media;

/// <summary>
/// A point on the monotonic media clock, in nanoseconds. Every capture time, presentation time and
/// deadline in StreamTransport is one, so audio and video line up without converting between clocks.
/// </summary>
/// <remarks>
/// Conversions from a platform timestamp go through 128-bit arithmetic: multiplying a tick count by a
/// billion overflows 64 bits after about 2.56 hours on hosts whose timestamp ticks are nanoseconds.
/// </remarks>
/// <param name="Nanoseconds">Nanoseconds on the monotonic clock.</param>
public readonly record struct MediaTime(long Nanoseconds) : IComparable<MediaTime>
{
    private const long NanosecondsPerSecond = 1_000_000_000;

    /// <summary>The clock's origin.</summary>
    public static MediaTime Zero => default;

    /// <summary>A <see cref="TimeProvider"/> timestamp as media time.</summary>
    /// <param name="timestamp">The timestamp, in ticks of <paramref name="frequency"/>.</param>
    /// <param name="frequency">Ticks per second.</param>
    /// <returns>The media time.</returns>
    public static MediaTime FromTimestamp(long timestamp, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        return new MediaTime((long)((Int128)timestamp * NanosecondsPerSecond / frequency));
    }

    /// <summary>A duration as nanoseconds.</summary>
    /// <param name="duration">The duration.</param>
    /// <returns>The equivalent offset from <see cref="Zero"/>.</returns>
    public static MediaTime FromTimeSpan(TimeSpan duration) =>
        new(duration.Ticks * (NanosecondsPerSecond / TimeSpan.TicksPerSecond));

    /// <summary>This time as a duration from <see cref="Zero"/>, to the 100 ns resolution of <see cref="TimeSpan"/>.</summary>
    /// <returns>The duration.</returns>
    public TimeSpan ToTimeSpan() =>
        TimeSpan.FromTicks(Nanoseconds / (NanosecondsPerSecond / TimeSpan.TicksPerSecond));

    /// <inheritdoc/>
    public int CompareTo(MediaTime other) => Nanoseconds.CompareTo(other.Nanoseconds);

    /// <summary>A later time.</summary>
    /// <param name="time">The time.</param>
    /// <param name="duration">How much later.</param>
    /// <returns>The sum.</returns>
    public static MediaTime operator +(MediaTime time, TimeSpan duration) =>
        new(time.Nanoseconds + FromTimeSpan(duration).Nanoseconds);

    /// <summary>An earlier time.</summary>
    /// <param name="time">The time.</param>
    /// <param name="duration">How much earlier.</param>
    /// <returns>The difference.</returns>
    public static MediaTime operator -(MediaTime time, TimeSpan duration) =>
        new(time.Nanoseconds - FromTimeSpan(duration).Nanoseconds);

    /// <summary>The span between two times.</summary>
    /// <param name="later">The later time.</param>
    /// <param name="earlier">The earlier time.</param>
    /// <returns>The span, negative when <paramref name="later"/> is earlier.</returns>
    public static TimeSpan operator -(MediaTime later, MediaTime earlier) =>
        TimeSpan.FromTicks(
            (later.Nanoseconds - earlier.Nanoseconds)
                / (NanosecondsPerSecond / TimeSpan.TicksPerSecond)
        );

    /// <summary>Whether one time is before another.</summary>
    /// <param name="left">The first time.</param>
    /// <param name="right">The second time.</param>
    /// <returns>Whether <paramref name="left"/> is earlier.</returns>
    public static bool operator <(MediaTime left, MediaTime right) =>
        left.Nanoseconds < right.Nanoseconds;

    /// <summary>Whether one time is after another.</summary>
    /// <param name="left">The first time.</param>
    /// <param name="right">The second time.</param>
    /// <returns>Whether <paramref name="left"/> is later.</returns>
    public static bool operator >(MediaTime left, MediaTime right) =>
        left.Nanoseconds > right.Nanoseconds;

    /// <summary>Whether one time is at or before another.</summary>
    /// <param name="left">The first time.</param>
    /// <param name="right">The second time.</param>
    /// <returns>Whether <paramref name="left"/> is not later.</returns>
    public static bool operator <=(MediaTime left, MediaTime right) =>
        left.Nanoseconds <= right.Nanoseconds;

    /// <summary>Whether one time is at or after another.</summary>
    /// <param name="left">The first time.</param>
    /// <param name="right">The second time.</param>
    /// <returns>Whether <paramref name="left"/> is not earlier.</returns>
    public static bool operator >=(MediaTime left, MediaTime right) =>
        left.Nanoseconds >= right.Nanoseconds;
}

/// <summary>The monotonic media clock, over a <see cref="System.TimeProvider"/> so tests can drive it.</summary>
/// <param name="timeProvider">The time source; the system's when null.</param>
public sealed class MediaClock(TimeProvider? timeProvider = null)
{
    /// <summary>The clock over the system's time.</summary>
    public static MediaClock System { get; } = new();

    /// <summary>The time source.</summary>
    public TimeProvider TimeProvider { get; } = timeProvider ?? TimeProvider.System;

    /// <summary>The current media time.</summary>
    public MediaTime Now =>
        MediaTime.FromTimestamp(TimeProvider.GetTimestamp(), TimeProvider.TimestampFrequency);
}

/// <summary>What a <see cref="MediaTimestamp"/> measures.</summary>
public enum TimestampKind
{
    /// <summary>When the producer captured the media, as it reported.</summary>
    Capture,

    /// <summary>When StreamTransport first saw the media, because the producer reported no capture time.</summary>
    Observation,
}

/// <summary>
/// When a frame was made, with what kind of time that is. A capture time and a time the frame was
/// merely observed are both media time, but only the first can be trusted for lip sync.
/// </summary>
/// <param name="Time">The time.</param>
/// <param name="Kind">Whether it is a capture or an observation time.</param>
/// <param name="ProducerLatency">
/// How long the producer said the media took to reach it before <see cref="Time"/>; zero when it said
/// nothing.
/// </param>
public readonly record struct MediaTimestamp(
    MediaTime Time,
    TimestampKind Kind,
    TimeSpan ProducerLatency = default
)
{
    /// <summary>A capture time.</summary>
    /// <param name="time">The time.</param>
    /// <returns>The timestamp.</returns>
    public static MediaTimestamp Captured(MediaTime time) => new(time, TimestampKind.Capture);

    /// <summary>An observation time.</summary>
    /// <param name="time">The time.</param>
    /// <returns>The timestamp.</returns>
    public static MediaTimestamp Observed(MediaTime time) => new(time, TimestampKind.Observation);

    /// <summary>When the media was made, as best known: the time less the producer's reported latency.</summary>
    public MediaTime Origin => Time - ProducerLatency;
}

/// <summary>A media clock rate: samples or ticks per second.</summary>
/// <param name="Hertz">Ticks per second.</param>
public readonly record struct ClockRate(int Hertz)
{
    /// <summary>The ticks in a span of time, rounded down.</summary>
    /// <param name="duration">The span.</param>
    /// <returns>The ticks.</returns>
    public long ToTicks(TimeSpan duration) =>
        (long)((Int128)duration.Ticks * Hertz / TimeSpan.TicksPerSecond);

    /// <summary>The time a number of ticks spans.</summary>
    /// <param name="ticks">The ticks.</param>
    /// <returns>The span.</returns>
    public TimeSpan ToTimeSpan(long ticks) =>
        TimeSpan.FromTicks((long)((Int128)ticks * TimeSpan.TicksPerSecond / Hertz));
}
