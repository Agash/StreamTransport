namespace Agash.StreamTransport.WebRtc.CongestionControl;

/// <summary>
/// A leaky-bucket pacing budget: it refills at the target rate and caps the burst, so an encoder's bursty
/// output (a large intra frame) is smoothed onto the link instead of being dumped at once — the key to
/// avoiding self-inflicted bufferbloat and loss on a cellular uplink. The async drain loop that pulls
/// queued packets lives in the integration layer; this is the allocation-free budget math it drives.
/// </summary>
public sealed class PacingBudget
{
    private readonly double _maxBurstMicros;
    private double _rateBytesPerSecond;
    private double _budgetBytes;
    private long _lastRefillMicros;
    private bool _primed;

    /// <summary>Creates a budget allowing up to <paramref name="maxBurstMs"/> of data to accumulate.</summary>
    public PacingBudget(long initialRateBps, double maxBurstMs = 40)
    {
        _rateBytesPerSecond = initialRateBps / 8.0;
        _maxBurstMicros = maxBurstMs * 1000.0;
    }

    /// <summary>Updates the pacing rate (from the congestion controller's pacing rate).</summary>
    public void SetRate(long pacingRateBps) => _rateBytesPerSecond = Math.Max(1, pacingRateBps / 8.0);

    /// <summary>
    /// Refills the budget for the elapsed time and returns the number of bytes that may be sent now,
    /// capped at the burst limit. Call before draining the pacing queue.
    /// </summary>
    public int Refill(long nowMicros)
    {
        if (!_primed)
        {
            _primed = true;
            _lastRefillMicros = nowMicros;
            return (int)Math.Max(0, _budgetBytes);
        }

        double elapsedMicros = Math.Max(0, nowMicros - _lastRefillMicros);
        _lastRefillMicros = nowMicros;

        _budgetBytes += _rateBytesPerSecond * (elapsedMicros / 1_000_000.0);
        double cap = _rateBytesPerSecond * (_maxBurstMicros / 1_000_000.0);
        _budgetBytes = Math.Min(_budgetBytes, cap);
        return (int)Math.Max(0, _budgetBytes);
    }

    /// <summary>Consumes <paramref name="bytes"/> from the budget after sending a packet.</summary>
    public void Consume(int bytes) => _budgetBytes -= bytes;
}
