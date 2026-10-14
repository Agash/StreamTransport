namespace Agash.StreamTransport.Adaptation.Tests;

// RFC 8083 section 4.
[TestClass]
public sealed class CircuitBreakerTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Rtt = TimeSpan.FromMilliseconds(50);

    // Section 4.1: three deterministic intervals of 5 s without feedback while sending.
    [TestMethod]
    public void OnTick_SendingWithoutFeedbackFor15Seconds_Opens()
    {
        CircuitBreaker breaker = new();
        breaker.OnSent(TimeSpan.Zero);
        breaker.OnFeedback(TimeSpan.Zero);
        for (int s = 1; s < 15; s++)
        {
            breaker.OnSent(s * Second);
            Assert.AreEqual(
                CircuitBreakerState.Closed,
                breaker.OnTick(s * Second),
                "a missed report is not a failure"
            );
        }

        breaker.OnSent(15 * Second);
        Assert.AreEqual(CircuitBreakerState.Open, breaker.OnTick(15 * Second));
        Assert.AreEqual(CircuitBreakerReason.FeedbackTimeout, breaker.Reason);
    }

    [TestMethod]
    public void OnTick_SilentWhileNotSending_StaysClosed()
    {
        CircuitBreaker breaker = new();
        breaker.OnSent(TimeSpan.Zero);
        breaker.OnFeedback(TimeSpan.Zero);

        Assert.AreEqual(CircuitBreakerState.Closed, breaker.OnTick(60 * Second));
    }

    // Section 4.2: k = 5 reports showing no progress while sending.
    [TestMethod]
    public void OnReceptionReport_FiveReportsWithoutProgress_Opens()
    {
        CircuitBreaker breaker = new();
        breaker.OnReceptionReport(Second, 100, 0, Rtt, 1_000_000, 1000);
        for (int i = 2; i <= 5; i++)
        {
            breaker.OnReceptionReport(i * Second, 100, 0, Rtt, 1_000_000, 1000);
            Assert.AreEqual(CircuitBreakerState.Closed, breaker.State);
        }

        breaker.OnReceptionReport(6 * Second, 100, 0, Rtt, 1_000_000, 1000);
        Assert.AreEqual(CircuitBreakerState.Open, breaker.State);
        Assert.AreEqual(CircuitBreakerReason.MediaTimeout, breaker.Reason);
    }

    [TestMethod]
    public void OnReceptionReport_StalledWhileIdle_StaysClosed()
    {
        CircuitBreaker breaker = new();
        for (int i = 1; i <= 10; i++)
        {
            breaker.OnReceptionReport(i * Second, 100, 0, Rtt, 0, 0);
        }

        Assert.AreEqual(CircuitBreakerState.Closed, breaker.State);
    }

    // Section 4.3: a rate above ten times the TCP equation's over CB_INTERVAL reports first reduces to a
    // tenth, then ceases if that does not resolve it.
    [TestMethod]
    public void OnReceptionReport_PersistentCongestion_ReducesThenOpens()
    {
        CircuitBreaker breaker = new();
        uint highest = 0;
        TimeSpan now = TimeSpan.Zero;

        // 20 % loss at 50 ms: TCP gets about 1000 B / (0.05 s * sqrt(0.4/3)) = 55 kB/s, 440 kbit/s.
        // 10 Mbit/s is more than ten times that.
        int reports = 0;
        while (breaker.State == CircuitBreakerState.Closed && reports++ < 100)
        {
            now += Second;
            breaker.OnReceptionReport(now, highest += 1000, 0.2, Rtt, 10_000_000, 1000);
        }

        Assert.AreEqual(CircuitBreakerState.Reduced, breaker.State);
        Assert.AreEqual(1_000_000, breaker.ReducedBitsPerSecond);
        Assert.AreEqual(
            15,
            reports,
            "CB_INTERVAL with a 2 s group of pictures is 15 one-second reports"
        );

        reports = 0;
        while (breaker.State == CircuitBreakerState.Reduced && reports++ < 100)
        {
            now += Second;
            breaker.OnReceptionReport(now, highest += 1000, 0.2, Rtt, 10_000_000, 1000);
        }

        Assert.AreEqual(CircuitBreakerState.Open, breaker.State);
        Assert.AreEqual(CircuitBreakerReason.Congestion, breaker.Reason);
    }

    [TestMethod]
    public void OnReceptionReport_ReductionResolvesIt_Closes()
    {
        CircuitBreaker breaker = new();
        uint highest = 0;
        TimeSpan now = TimeSpan.Zero;
        while (breaker.State == CircuitBreakerState.Closed)
        {
            now += Second;
            breaker.OnReceptionReport(now, highest += 1000, 0.2, Rtt, 10_000_000, 1000);
        }

        // At a tenth of the rate the loss clears.
        for (int i = 0; i < 20 && breaker.State == CircuitBreakerState.Reduced; i++)
        {
            now += Second;
            breaker.OnReceptionReport(now, highest += 100, 0, Rtt, 1_000_000, 1000);
        }

        Assert.AreEqual(CircuitBreakerState.Closed, breaker.State);
    }

    // Section 4.5: no restart until as long as it took to trip has passed again.
    [TestMethod]
    public void TryReset_BeforeTheTriggeringInterval_IsRefused()
    {
        CircuitBreaker breaker = new();
        breaker.OnSent(TimeSpan.Zero);
        breaker.OnFeedback(TimeSpan.Zero);
        breaker.OnSent(15 * Second);
        _ = breaker.OnTick(15 * Second);

        Assert.IsFalse(breaker.TryReset(20 * Second));
        Assert.IsTrue(breaker.TryReset(30 * Second));
        Assert.AreEqual(CircuitBreakerState.Closed, breaker.State);
    }
}
