namespace Agash.StreamTransport.Media;

/// <summary>
/// A wall-clock instant as NTP writes it (RFC 5905): a UQ32.32 count of seconds since 1900. A sender's
/// capture instants reach the receiver in this form, whatever carries them (RTP's abs-capture-time, a
/// sender report).
/// </summary>
/// <param name="Value">The UQ32.32 timestamp.</param>
public readonly record struct NtpTime(ulong Value)
{
    private const long NanosecondsPerSecond = 1_000_000_000;
    private static readonly DateTimeOffset Epoch = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Nanoseconds since the NTP epoch.</summary>
    public long Nanoseconds =>
        (long)(Value >> 32) * NanosecondsPerSecond
        + (long)(((Value & 0xFFFF_FFFF) * NanosecondsPerSecond) >> 32);

    /// <summary>A wall-clock instant as NTP time.</summary>
    /// <param name="time">The instant, after 1900.</param>
    /// <returns>The NTP time.</returns>
    public static NtpTime From(DateTimeOffset time) => FromNanoseconds((time - Epoch).Ticks * 100);

    /// <summary>NTP time from nanoseconds since the NTP epoch.</summary>
    /// <param name="nanoseconds">Nanoseconds since 1900.</param>
    /// <returns>The NTP time.</returns>
    public static NtpTime FromNanoseconds(long nanoseconds)
    {
        ulong seconds = (ulong)(nanoseconds / NanosecondsPerSecond);
        ulong remainder = (ulong)(nanoseconds % NanosecondsPerSecond);
        return new NtpTime((seconds << 32) | ((remainder << 32) / NanosecondsPerSecond));
    }

    /// <summary>A later instant.</summary>
    /// <param name="time">The instant.</param>
    /// <param name="duration">How much later.</param>
    /// <returns>The sum.</returns>
    public static NtpTime operator +(NtpTime time, TimeSpan duration) =>
        FromNanoseconds(time.Nanoseconds + (duration.Ticks * 100));
}
