namespace Agash.StreamTransport.Adaptation;

/// <summary>
/// Splits the rate a transport allows between audio, recovery traffic and video. Audio and recovery
/// (retransmissions and FEC repair) are measured from what was sent, so the encoder's target leaves room
/// for them: retransmitted and repair bytes count against the same budget as the media
/// (RFC 4588 section 7).
/// </summary>
public sealed class MediaRateAllocator
{
    // The averages' time constant: one burst of repairs does not swing the encoder, and a lasting change
    // is most of the way in after a few of these.
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinimumSample = TimeSpan.FromMilliseconds(100);

    private readonly Func<TrafficClass, long> _sentBytes;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private long _sampledAt;
    private long _audioBytes;
    private long _recoveryBytes;
    private double _audioBitsPerSecond;
    private double _recoveryBitsPerSecond;

    /// <summary>An allocator reading the bytes sent per class.</summary>
    /// <param name="sentBytes">The bytes sent so far in a class (a pacer's or a transport's count).</param>
    /// <param name="timeProvider">The clock the rates are measured on; the system's when null.</param>
    public MediaRateAllocator(Func<TrafficClass, long> sentBytes, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sentBytes);
        _sentBytes = sentBytes;
        _time = timeProvider ?? TimeProvider.System;
        _sampledAt = _time.GetTimestamp();
        (_audioBytes, _recoveryBytes) = Sample();
    }

    /// <summary>The recovery traffic's measured rate, in bits per second.</summary>
    public long RecoveryBitsPerSecond
    {
        get
        {
            lock (_gate)
            {
                Update();
                return (long)_recoveryBitsPerSecond;
            }
        }
    }

    /// <summary>
    /// Video's share of a target: what remains after audio (the larger of its configured and measured
    /// rate) and the measured recovery traffic, and never below a floor.
    /// </summary>
    /// <param name="targetBitsPerSecond">The rate the transport allows.</param>
    /// <param name="audioBitsPerSecond">The audio encoder's configured rate, or zero without audio.</param>
    /// <param name="minimumVideoBitsPerSecond">The least video gets.</param>
    /// <returns>Video's rate.</returns>
    public long VideoBitsPerSecond(
        long targetBitsPerSecond,
        long audioBitsPerSecond,
        long minimumVideoBitsPerSecond
    )
    {
        lock (_gate)
        {
            Update();
            double audio =
                audioBitsPerSecond == 0 ? 0 : Math.Max(audioBitsPerSecond, _audioBitsPerSecond);
            return Math.Max(
                minimumVideoBitsPerSecond,
                (long)(targetBitsPerSecond - audio - _recoveryBitsPerSecond)
            );
        }
    }

    private (long Audio, long Recovery) Sample() =>
        (
            _sentBytes(TrafficClass.Audio),
            _sentBytes(TrafficClass.Retransmission) + _sentBytes(TrafficClass.Repair)
        );

    // Folds the bytes sent since the last sample into the averages, once enough time has passed.
    private void Update()
    {
        TimeSpan elapsed = _time.GetElapsedTime(_sampledAt);
        if (elapsed < MinimumSample)
        {
            return;
        }

        (long audio, long recovery) = Sample();
        double seconds = elapsed.TotalSeconds;
        // An exponential average in time, so the result does not depend on how often it is sampled.
        double weight = 1 - Math.Exp(-(elapsed / Window));
        _audioBitsPerSecond += weight * ((audio - _audioBytes) * 8 / seconds - _audioBitsPerSecond);
        _recoveryBitsPerSecond +=
            weight * ((recovery - _recoveryBytes) * 8 / seconds - _recoveryBitsPerSecond);
        _audioBytes = audio;
        _recoveryBytes = recovery;
        _sampledAt = _time.GetTimestamp();
    }
}
