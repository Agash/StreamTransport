using Agash.StreamTransport.Adaptation;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.WebRtc;

// RFC 8083 circuit breakers, one per stream this endpoint sends: any RTCP from the peer counts as feedback,
// and the reception blocks about each stream feed its media-timeout and congestion checks. When any opens,
// media on the connection stops (section 4.5: the 5-tuple ceases) until the application resumes it; while
// one is reduced, the capacity is capped at a tenth of the rate that tripped it.
public sealed partial class PeerConnection
{
    private readonly Lock _breakerGate = new();
    private readonly Dictionary<uint, StreamBreaker> _breakers = [];

    /// <summary>Raised when the connection's circuit breaker state changes, with the reason.</summary>
    public event Action<CircuitBreakerState, CircuitBreakerReason>? CircuitBreakerChanged;

    /// <summary>
    /// Whether media flows: <see cref="CircuitBreakerState.Open"/> means transmission ceased after a
    /// persistent failure (RFC 8083), and <see cref="TrySendRtp"/> drops media until
    /// <see cref="TryResumeTransmission"/>.
    /// </summary>
    public CircuitBreakerState CircuitBreakerState { get; private set; }

    /// <summary>
    /// Resumes media after the circuit breaker opened, when the application has reason to think the problem
    /// passed (a user's decision, a new network path). Refused until as long as it took to trip has passed
    /// again (RFC 8083 section 4.5).
    /// </summary>
    /// <returns>Whether media flows again.</returns>
    public bool TryResumeTransmission()
    {
        TimeSpan now = Now;
        lock (_breakerGate)
        {
            foreach (StreamBreaker stream in _breakers.Values)
            {
                if (!stream.Breaker.TryReset(now))
                {
                    return false;
                }
            }
        }

        UpdateBreakerState(CircuitBreakerReason.None);
        return true;
    }

    private bool TransmissionCeased => CircuitBreakerState == CircuitBreakerState.Open;

    private void BreakerOnSent(uint ssrc, int bytes)
    {
        TimeSpan now = Now;
        lock (_breakerGate)
        {
            if (!_breakers.TryGetValue(ssrc, out StreamBreaker? stream))
            {
                stream = new StreamBreaker(new CircuitBreaker(_options.CircuitBreaker), now);
                _breakers[ssrc] = stream;
            }

            stream.Breaker.OnSent(now);
            stream.Bytes += bytes;
            stream.Packets++;
        }
    }

    private void BreakerOnFeedback()
    {
        TimeSpan now = Now;
        lock (_breakerGate)
        {
            foreach (StreamBreaker stream in _breakers.Values)
            {
                stream.Breaker.OnFeedback(now);
            }
        }
    }

    // A reception block about one of this endpoint's streams.
    private void BreakerOnReport(
        uint ssrc,
        uint extendedHighest,
        byte fractionLost,
        TimeSpan roundTrip
    )
    {
        TimeSpan now = Now;
        CircuitBreakerReason reason;
        lock (_breakerGate)
        {
            if (!_breakers.TryGetValue(ssrc, out StreamBreaker? stream))
            {
                return;
            }

            // The rate and mean packet size since this stream's previous report.
            double seconds = (now - stream.Since).TotalSeconds;
            long rate = seconds > 0 ? (long)(stream.Bytes * 8 / seconds) : 0;
            int packetSize = stream.Packets > 0 ? (int)(stream.Bytes / stream.Packets) : 0;
            stream.Bytes = 0;
            stream.Packets = 0;
            stream.Since = now;
            stream.Breaker.OnReceptionReport(
                now,
                extendedHighest,
                fractionLost / 256.0,
                roundTrip,
                rate,
                packetSize
            );
            reason = stream.Breaker.Reason;
        }

        UpdateBreakerState(reason);
    }

    private void BreakerOnTick()
    {
        TimeSpan now = Now;
        CircuitBreakerReason reason = CircuitBreakerReason.None;
        lock (_breakerGate)
        {
            foreach (StreamBreaker stream in _breakers.Values)
            {
                if (stream.Breaker.OnTick(now) != CircuitBreakerState.Closed)
                {
                    reason = stream.Breaker.Reason;
                }
            }
        }

        UpdateBreakerState(reason);
    }

    // The connection takes its streams' worst state.
    private void UpdateBreakerState(CircuitBreakerReason reason)
    {
        CircuitBreakerState state = CircuitBreakerState.Closed;
        lock (_breakerGate)
        {
            foreach (StreamBreaker stream in _breakers.Values)
            {
                if (stream.Breaker.State > state)
                {
                    state = stream.Breaker.State;
                    reason = stream.Breaker.Reason;
                }
            }
        }

        if (state == CircuitBreakerState)
        {
            return;
        }

        CircuitBreakerState = state;
        LogCircuitBreaker(_logger, state, reason);
        CircuitBreakerChanged?.Invoke(state, reason);
    }

    // While reduced, the capacity the pacer and the media layer see is held to the breaker's cap.
    private CapacityEstimate ApplyBreakerCap(CapacityEstimate estimate)
    {
        if (CircuitBreakerState != CircuitBreakerState.Reduced)
        {
            return estimate;
        }

        long cap = long.MaxValue;
        lock (_breakerGate)
        {
            foreach (StreamBreaker stream in _breakers.Values)
            {
                if (stream.Breaker.ReducedBitsPerSecond > 0)
                {
                    cap = Math.Min(cap, stream.Breaker.ReducedBitsPerSecond);
                }
            }
        }

        return cap == long.MaxValue
            ? estimate
            : estimate with
            {
                TargetBitsPerSecond = Math.Min(estimate.TargetBitsPerSecond, cap),
                PacingBitsPerSecond = Math.Min(estimate.PacingBitsPerSecond, cap * 5 / 4),
            };
    }

    private sealed class StreamBreaker(CircuitBreaker breaker, TimeSpan since)
    {
        public CircuitBreaker Breaker { get; } = breaker;

        public TimeSpan Since { get; set; } = since;

        public long Bytes { get; set; }

        public long Packets { get; set; }
    }

    [LoggerMessage(
        EventId = 1159,
        Level = LogLevel.Warning,
        Message = "Circuit breaker {State}: {Reason}"
    )]
    private static partial void LogCircuitBreaker(
        ILogger logger,
        CircuitBreakerState state,
        CircuitBreakerReason reason
    );
}
