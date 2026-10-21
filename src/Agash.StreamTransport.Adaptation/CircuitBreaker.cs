namespace Agash.StreamTransport.Adaptation;

/// <summary>Whether a <see cref="CircuitBreaker"/> lets media through.</summary>
public enum CircuitBreakerState
{
    /// <summary>Media flows at whatever the congestion controller allows.</summary>
    Closed,

    /// <summary>
    /// Congestion persisted: media is held to a tenth of the rate that tripped it, to see whether that
    /// resolves it (RFC 8083 section 4.3).
    /// </summary>
    Reduced,

    /// <summary>Transmission has ceased until the application resumes it (RFC 8083 section 4.5).</summary>
    Open,
}

/// <summary>Why a <see cref="CircuitBreaker"/> left <see cref="CircuitBreakerState.Closed"/>.</summary>
public enum CircuitBreakerReason
{
    /// <summary>It has not tripped.</summary>
    None,

    /// <summary>No feedback came back for three reporting intervals: the receiver or the return path is gone.</summary>
    FeedbackTimeout,

    /// <summary>Feedback keeps showing that the media sent is not arriving: the forward path failed.</summary>
    MediaTimeout,

    /// <summary>The send rate stayed above ten times what TCP would get on the path.</summary>
    Congestion,
}

/// <summary>Tunables for a <see cref="CircuitBreaker"/>, defaulting to RFC 8083's values.</summary>
public sealed class CircuitBreakerOptions
{
    /// <summary>
    /// The deterministic reporting interval Td, at least the five seconds of RFC 3550's minimum; feedback
    /// silent for three of these trips the breaker.
    /// </summary>
    public TimeSpan ReportingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The receiver's reception-report interval Tdr, about a second here.</summary>
    public TimeSpan ReceiverReportInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The time a group of pictures spans (G times Tf), the keyframe interval.</summary>
    public TimeSpan GroupOfPictures { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Consecutive reports showing no progress that trip the media timeout (k, RECOMMENDED 5).</summary>
    public int MediaTimeoutReports { get; set; } = 5;

    /// <summary>
    /// Whether the congestion breaker uses the full TCP throughput equation rather than the simplified one
    /// RFC 8083 recommends; the full one reacts earlier, which measurements found better balanced on LTE.
    /// </summary>
    public bool FullThroughputEquation { get; set; }
}

/// <summary>
/// The RTP circuit breakers of RFC 8083, kept apart from congestion control: the controller decides the rate;
/// this decides when sending is no longer safe or useful. One missed report does nothing, and intermittent
/// feedback loss is the controller's to absorb; three silent reporting intervals (a vanished receiver or
/// return path), reports that keep showing nothing arriving (a failed forward path), or a rate an order of
/// magnitude above what TCP would get (persistent congestion) trip it. Congestion first reduces media to a
/// tenth; if that does not resolve it within the next window, transmission ceases. Once open, it stays open
/// until <see cref="TryReset"/>, which refuses before the triggering interval has passed (section 4.5).
/// </summary>
/// <param name="options">The tunables; RFC 8083's defaults when null.</param>
/// <remarks>Not thread-safe: the transport feeds it from one place.</remarks>
public sealed class CircuitBreaker(CircuitBreakerOptions? options = null)
{
    private readonly CircuitBreakerOptions _options = options ?? new CircuitBreakerOptions();
    private readonly Queue<(double FractionLost, TimeSpan Interval)> _losses = new();
    private TimeSpan? _lastFeedback;
    private TimeSpan? _lastSent;
    private TimeSpan _lastReport;
    private uint _highestReported;
    private bool _haveHighest;
    private int _stalledReports;
    private TimeSpan _trippedAt;
    private TimeSpan _triggeringInterval;
    private TimeSpan _reducedAt;

    /// <summary>The current state.</summary>
    public CircuitBreakerState State { get; private set; }

    /// <summary>Why it last tripped.</summary>
    public CircuitBreakerReason Reason { get; private set; }

    /// <summary>
    /// The cap on the media rate while <see cref="CircuitBreakerState.Reduced"/>: a tenth of the rate that
    /// tripped it; zero otherwise.
    /// </summary>
    public long ReducedBitsPerSecond { get; private set; }

    /// <summary>Notes that media went out.</summary>
    /// <param name="now">Now, on the sender's monotonic clock.</param>
    public void OnSent(TimeSpan now)
    {
        _lastSent = now;
        _lastFeedback ??= now;
    }

    /// <summary>Notes that feedback about this sender arrived, of any kind (reduced-size included).</summary>
    /// <param name="now">Now, on the sender's monotonic clock.</param>
    public void OnFeedback(TimeSpan now) => _lastFeedback = now;

    /// <summary>
    /// Folds in a reception report about this sender's media: how far it got and what fraction was lost in
    /// the interval it covers.
    /// </summary>
    /// <param name="now">Now, on the sender's monotonic clock.</param>
    /// <param name="extendedHighestSequence">The highest sequence number the receiver had, extended.</param>
    /// <param name="fractionLost">The loss over the report's interval, 0 to 1.</param>
    /// <param name="roundTrip">The round trip.</param>
    /// <param name="sendBitsPerSecond">The rate the stream was sent at since its previous report; zero when idle.</param>
    /// <param name="packetSize">The mean packet size, in bytes.</param>
    public void OnReceptionReport(
        TimeSpan now,
        uint extendedHighestSequence,
        double fractionLost,
        TimeSpan roundTrip,
        long sendBitsPerSecond,
        int packetSize
    )
    {
        _lastFeedback = now;
        if (State == CircuitBreakerState.Open)
        {
            return;
        }

        // Both checks apply only to a stream that is sending (sections 4.2 and 4.3): an idle stream's
        // reports cannot show progress.
        if (sendBitsPerSecond <= 0)
        {
            _lastReport = now;
            return;
        }

        // Media timeout (section 4.2): reports that keep showing no progress while media is sent.
        bool progressed = !_haveHighest || (int)(extendedHighestSequence - _highestReported) > 0;
        _highestReported = extendedHighestSequence;
        _haveHighest = true;
        _stalledReports = progressed ? 0 : _stalledReports + 1;
        if (_stalledReports >= MediaTimeoutReports(roundTrip))
        {
            Trip(
                now,
                CircuitBreakerReason.MediaTimeout,
                _options.ReceiverReportInterval * _stalledReports
            );
            return;
        }

        // Congestion (section 4.3): the average loss over the last CB_INTERVAL reports against the rate.
        TimeSpan interval =
            _lastReport == TimeSpan.Zero ? _options.ReceiverReportInterval : now - _lastReport;
        _lastReport = now;
        int window = CongestionWindowReports(roundTrip);
        _losses.Enqueue((Math.Clamp(fractionLost, 0, 1), interval));
        while (_losses.Count > window)
        {
            _ = _losses.Dequeue();
        }

        if (_losses.Count < window || roundTrip <= TimeSpan.Zero || packetSize <= 0)
        {
            return;
        }

        double weighted = 0;
        double total = 0;
        foreach ((double lost, TimeSpan span) in _losses)
        {
            weighted += lost * span.TotalSeconds;
            total += span.TotalSeconds;
        }

        double p = total > 0 ? weighted / total : 0;
        double tcpBitsPerSecond = TcpThroughput(packetSize, roundTrip.TotalSeconds, p) * 8;
        var windowSpan = TimeSpan.FromSeconds(total);
        if (sendBitsPerSecond <= 10 * tcpBitsPerSecond)
        {
            // Resolved: a reduction that held for a full window is lifted.
            if (State == CircuitBreakerState.Reduced && now - _reducedAt >= windowSpan)
            {
                State = CircuitBreakerState.Closed;
                Reason = CircuitBreakerReason.None;
                ReducedBitsPerSecond = 0;
            }

            return;
        }

        if (State == CircuitBreakerState.Reduced && now - _reducedAt >= windowSpan)
        {
            Trip(now, CircuitBreakerReason.Congestion, windowSpan);
        }
        else if (State == CircuitBreakerState.Closed)
        {
            State = CircuitBreakerState.Reduced;
            Reason = CircuitBreakerReason.Congestion;
            ReducedBitsPerSecond = Math.Max(1, sendBitsPerSecond / 10);
            _reducedAt = now;
            _losses.Clear();
        }
    }

    /// <summary>Checks the feedback timeout as time passes.</summary>
    /// <param name="now">Now, on the sender's monotonic clock.</param>
    /// <returns>The state.</returns>
    public CircuitBreakerState OnTick(TimeSpan now)
    {
        // RTCP timeout (section 4.1): sending, and nothing back for three deterministic intervals.
        TimeSpan timeout = _options.ReportingInterval * 3;
        if (
            State != CircuitBreakerState.Open
            && _lastSent is { } sent
            && _lastFeedback is { } heard
            && now - sent < timeout
            && now - heard >= timeout
        )
        {
            Trip(now, CircuitBreakerReason.FeedbackTimeout, now - heard);
        }

        return State;
    }

    /// <summary>
    /// Closes an open breaker so media can flow again, when the application has reason to think the problem
    /// has passed (a user's decision, a new network). Refused until the time it took to trip has passed again.
    /// </summary>
    /// <param name="now">Now, on the sender's monotonic clock.</param>
    /// <returns>Whether it closed.</returns>
    public bool TryReset(TimeSpan now)
    {
        if (State == CircuitBreakerState.Open && now - _trippedAt < _triggeringInterval)
        {
            return false;
        }

        State = CircuitBreakerState.Closed;
        Reason = CircuitBreakerReason.None;
        ReducedBitsPerSecond = 0;
        _stalledReports = 0;
        _haveHighest = false;
        _losses.Clear();
        _lastFeedback = now;
        return true;
    }

    private void Trip(TimeSpan now, CircuitBreakerReason reason, TimeSpan triggeringInterval)
    {
        State = CircuitBreakerState.Open;
        Reason = reason;
        ReducedBitsPerSecond = 0;
        _trippedAt = now;
        _triggeringInterval = triggeringInterval;
    }

    // MEDIA_TIMEOUT = ceil(k * max(Tf, Tr, Tdr) / Tdr); frames come far faster than reports here.
    private int MediaTimeoutReports(TimeSpan roundTrip)
    {
        double tdr = _options.ReceiverReportInterval.TotalSeconds;
        return (int)
            Math.Ceiling(
                _options.MediaTimeoutReports * Math.Max(roundTrip.TotalSeconds, tdr) / tdr
            );
    }

    // CB_INTERVAL = ceil(3 * min(max(10 G Tf, 10 Tr, 3 Tdr), max(15, 3 Td)) / (3 Tdr)).
    private int CongestionWindowReports(TimeSpan roundTrip)
    {
        double tdr = _options.ReceiverReportInterval.TotalSeconds;
        double td = _options.ReportingInterval.TotalSeconds;
        double span = Math.Min(
            Math.Max(
                Math.Max(10 * _options.GroupOfPictures.TotalSeconds, 10 * roundTrip.TotalSeconds),
                3 * tdr
            ),
            Math.Max(15, 3 * td)
        );
        return Math.Max(1, (int)Math.Ceiling(3 * span / (3 * tdr)));
    }

    // TCP Reno throughput in bytes per second (Padhye, as in RFC 5348; b = 1, t_RTO = 4 Tr), or the
    // simplified form RFC 8083 recommends.
    private double TcpThroughput(int packetSize, double roundTrip, double p)
    {
        if (p <= 0)
        {
            return double.PositiveInfinity;
        }

        double denominator = roundTrip * Math.Sqrt(2 * p / 3);
        if (_options.FullThroughputEquation)
        {
            denominator += 4 * roundTrip * (3 * Math.Sqrt(3 * p / 8)) * p * (1 + (32 * p * p));
        }

        return packetSize / denominator;
    }
}
