namespace Agash.StreamTransport.WebRtc.Rtcp;

/// <summary>
/// RTCP's share of the session (RFC 3550 section 6.2, RFC 3556): bytes per second for every participant's
/// RTCP together, and the fraction of it kept for active senders.
/// </summary>
/// <param name="BytesPerSecond">The RTCP bandwidth.</param>
/// <param name="SenderFraction">The senders' share, 0 to 1.</param>
internal readonly record struct RtcpBandwidth(double BytesPerSecond, double SenderFraction)
{
    // RFC 3550 section 6.2 and RFC 3556 section 3: RTCP is 5% of the session, a quarter of it the senders'.
    private const double RtcpFraction = 0.05;
    private const double DefaultSenderFraction = 0.25;

    /// <summary>
    /// The RTCP bandwidth of a session (RFC 3556 sections 2 and 3): RS plus RR when both are given;
    /// otherwise 5% of the session bandwidth, a missing RS or RR being the rest of it, and RS a quarter
    /// when neither is given.
    /// </summary>
    /// <param name="sessionBitsPerSecond">The session bandwidth.</param>
    /// <param name="senders">RS, in bits per second, when signalled.</param>
    /// <param name="receivers">RR, in bits per second, when signalled.</param>
    /// <returns>The bandwidth.</returns>
    public static RtcpBandwidth From(long sessionBitsPerSecond, long? senders, long? receivers)
    {
        double total =
            senders is { } s && receivers is { } r ? s + r : sessionBitsPerSecond * RtcpFraction;
        double rs =
            senders
            ?? (
                receivers is { } given ? Math.Max(0, total - given) : total * DefaultSenderFraction
            );
        return total <= 0 ? new(0, 0) : new(total / 8, Math.Clamp(rs / total, 0, 1));
    }
}

/// <summary>How many take part in the session as the scheduler counts them (RFC 3550 section 6.3).</summary>
/// <param name="Members">Every source heard or sent from, this side's included.</param>
/// <param name="Senders">The sources that sent RTP within the last two intervals.</param>
/// <param name="WeSent">Whether this side is among the senders.</param>
internal readonly record struct RtcpMembership(int Members, int Senders, bool WeSent);

/// <summary>
/// When this endpoint sends its regular RTCP report (RFC 3550 sections 6.2 and 6.3, appendix A.7, with the
/// RTP/AVPF changes of RFC 4585 sections 3.4, 3.5.1 and 3.5.3). The interval scales with the members and
/// the average packet size against RTCP's bandwidth, is randomized and compensated, and is reconsidered
/// when it expires and when members leave. Every local source reports in the one compound packet (RFC 8108
/// section 5.3), so each counts as a member and the average size is per reporting source (section 5.3.1).
/// The AVPF minimum is 0 between two peers (RFC 4585 section 3.5.1), with
/// <see cref="RegularInterval"/> as the lower bound between regular reports (T_rr_interval, section 3.5.3).
/// </summary>
internal sealed class RtcpScheduler
{
    // RFC 3550 appendix A.7: e - 3/2, compensating timer reconsideration's bias toward short intervals.
    private const double Compensation = Math.E - 1.5;

    // RFC 3550 section 6.2: the fixed minimum, which the participant timeout uses even where the reports
    // themselves use a smaller one (RFC 8834 section 4.1).
    private const long FixedMinimumMicros = 5_000_000;

    // RFC 3550 section 6.3.5: a member silent for this many deterministic intervals is gone.
    private const int TimeoutIntervals = 5;

    // A timer cannot fire sooner than this; it bounds the interval at very high RTCP bandwidth.
    private const long TimerGranularityMicros = 1_000;

    private readonly Func<double> _random;
    private long _lastSent = -1;

    /// <summary>A scheduler.</summary>
    /// <param name="regularInterval">T_rr_interval; zero for none.</param>
    /// <param name="random">Uniform in [0, 1); the shared generator when null.</param>
    public RtcpScheduler(TimeSpan regularInterval, Func<double>? random = null)
    {
        RegularInterval = regularInterval;
        _random = random ?? Random.Shared.NextDouble;
    }

    /// <summary>The least time between regular reports (RFC 4585 section 3.5.3's T_rr_interval).</summary>
    public TimeSpan RegularInterval { get; }

    /// <summary>
    /// The average compound packet size per reporting source, in bytes with the UDP and IP headers
    /// (RFC 3550 section 6.3.3, RFC 8108 section 5.3.1); an initial guess until packets are counted.
    /// </summary>
    public double AverageSize { get; private set; } = 100;

    /// <summary>When the next report is due, in microseconds on the connection clock; -1 before the first.</summary>
    public long Next { get; private set; } = -1;

    /// <summary>Counts a sent or received RTCP packet into the average size.</summary>
    /// <param name="bytes">Its size, with UDP and IP headers.</param>
    /// <param name="reportingSources">The sources with a sender or receiver report in it; 1 for none.</param>
    public void Count(int bytes, int reportingSources) =>
        AverageSize += ((bytes / (double)Math.Max(1, reportingSources)) - AverageSize) / 16;

    /// <summary>
    /// Schedules the first report: due at once between two peers (RFC 4585 section 3.5.1 sets the minimum
    /// to 0, RFC 8108 section 5.2 allows it as one aggregated packet), after a randomized interval with a
    /// 1 s minimum in a larger session.
    /// </summary>
    /// <param name="now">The time.</param>
    /// <param name="membership">The members.</param>
    /// <param name="bandwidth">RTCP's bandwidth.</param>
    public void Start(long now, RtcpMembership membership, RtcpBandwidth bandwidth) =>
        Next =
            membership.Members <= 2 && _lastSent < 0
                ? now
                : now
                    + Randomized(Deterministic(membership, bandwidth, InitialMinimum(membership)));

    /// <summary>
    /// The timer expired (RFC 3550 section 6.3.6 with timer reconsideration): whether to send now. When it
    /// does not, <see cref="Next"/> moves to the reconsidered time.
    /// </summary>
    /// <param name="now">The time.</param>
    /// <param name="membership">The members.</param>
    /// <param name="bandwidth">RTCP's bandwidth.</param>
    /// <returns>Whether to send the report now.</returns>
    public bool Expire(long now, RtcpMembership membership, RtcpBandwidth bandwidth)
    {
        if (bandwidth.BytesPerSecond <= 0)
        {
            // RFC 3556 section 2: no RTCP bandwidth at all. RTCP still flows (RFC 8834 section 4.1), at the
            // regular interval alone.
            Next = now + Math.Max(RegularIntervalMicros, FixedMinimumMicros);
            return _lastSent < 0;
        }

        long interval = Randomized(Deterministic(membership, bandwidth, minimumMicros: 0));
        if (_lastSent >= 0 && _lastSent + interval > now)
        {
            Next = _lastSent + interval;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Records a sent regular report and schedules the next: a fresh randomized interval, and no sooner
    /// than a dithered T_rr_interval (RFC 4585 section 3.5.3, as GStreamer's rtpsession applies it).
    /// </summary>
    /// <param name="now">The time it was sent.</param>
    /// <param name="membership">The members.</param>
    /// <param name="bandwidth">RTCP's bandwidth.</param>
    public void Sent(long now, RtcpMembership membership, RtcpBandwidth bandwidth)
    {
        _lastSent = now;
        long interval =
            bandwidth.BytesPerSecond <= 0
                ? Math.Max(RegularIntervalMicros, FixedMinimumMicros)
                : Randomized(Deterministic(membership, bandwidth, minimumMicros: 0));
        if (RegularIntervalMicros > 0)
        {
            interval = Math.Max(interval, (long)((0.5 + _random()) * RegularIntervalMicros));
        }

        Next = now + interval;
    }

    /// <summary>
    /// Members left (a BYE or a timeout): RFC 3550 section 6.3.4's reverse reconsideration pulls the next
    /// report and the last one toward now in proportion, so a shrunk session reports sooner.
    /// </summary>
    /// <param name="now">The time.</param>
    /// <param name="members">The members now.</param>
    /// <param name="previous">The members before.</param>
    public void MembersLeft(long now, int members, int previous)
    {
        if (members >= previous || previous == 0 || Next < 0)
        {
            return;
        }

        double ratio = members / (double)previous;
        Next = now + (long)(ratio * Math.Max(0, Next - now));
        if (_lastSent >= 0)
        {
            _lastSent = now - (long)(ratio * Math.Max(0, now - _lastSent));
        }
    }

    /// <summary>
    /// How long a member may stay silent before it is gone (RFC 3550 section 6.3.5): five deterministic
    /// intervals at the fixed 5 s minimum, whatever T_rr_interval is (RFC 8108 section 7.1.4, which updates
    /// RFC 4585 section 3.5.4 so every profile times out alike).
    /// </summary>
    /// <param name="membership">The members.</param>
    /// <param name="bandwidth">RTCP's bandwidth.</param>
    /// <returns>The timeout in microseconds.</returns>
    public long MemberTimeout(RtcpMembership membership, RtcpBandwidth bandwidth) =>
        TimeoutIntervals * Deterministic(membership, bandwidth, FixedMinimumMicros);

    /// <summary>
    /// How long a sender may go without RTP before it counts as a receiver (RFC 3550 section 6.3.8): two
    /// deterministic intervals, and no less than the fixed 5 s minimum, as GStreamer's rtpsession keeps it.
    /// </summary>
    /// <param name="membership">The members.</param>
    /// <param name="bandwidth">RTCP's bandwidth.</param>
    /// <returns>The timeout in microseconds.</returns>
    public long SenderTimeout(RtcpMembership membership, RtcpBandwidth bandwidth) =>
        Math.Max(2 * Deterministic(membership, bandwidth, minimumMicros: 0), FixedMinimumMicros);

    private long RegularIntervalMicros => RegularInterval.Ticks / TimeSpan.TicksPerMicrosecond;

    // RFC 4585 section 3.5.1: 0 between two peers, 1 s before the first report in a larger session.
    private static long InitialMinimum(RtcpMembership membership) =>
        membership.Members <= 2 ? 0 : 1_000_000;

    // RFC 3550 appendix A.7 before randomization: the senders' share when they are few, else everyone's.
    private long Deterministic(
        RtcpMembership membership,
        RtcpBandwidth bandwidth,
        long minimumMicros
    )
    {
        if (bandwidth.BytesPerSecond <= 0)
        {
            return Math.Max(minimumMicros, FixedMinimumMicros);
        }

        double rtcp = bandwidth.BytesPerSecond;
        double n = Math.Max(1, membership.Members);
        if (membership.Senders <= membership.Members * bandwidth.SenderFraction)
        {
            if (membership.WeSent)
            {
                rtcp *= bandwidth.SenderFraction;
                n = Math.Max(1, membership.Senders);
            }
            else
            {
                rtcp *= 1 - bandwidth.SenderFraction;
                n = Math.Max(1, membership.Members - membership.Senders);
            }
        }

        if (rtcp <= 0)
        {
            return Math.Max(minimumMicros, FixedMinimumMicros);
        }

        double seconds = AverageSize * n / rtcp;
        long micros = seconds >= long.MaxValue / 1e6 ? long.MaxValue / 4 : (long)(seconds * 1e6);
        return Math.Max(Math.Max(micros, minimumMicros), TimerGranularityMicros);
    }

    // Appendix A.7: uniform over half to one and a half times, divided by the compensation.
    private long Randomized(long deterministic) =>
        Math.Max(TimerGranularityMicros, (long)(deterministic * (0.5 + _random()) / Compensation));
}
