using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Streams;

/// <summary>
/// The rate a source delivers frames at, from their capture times over the last second. Rate control
/// plans each frame's bits from it, so a source running faster than planned does not overshoot the
/// target and fill the pacer's queue.
/// </summary>
internal sealed class FrameRateMeter(double initial)
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly Queue<MediaTime> _times = new();

    /// <summary>The rate measured, or the initial one until a second of frames has been seen.</summary>
    public double FramesPerSecond { get; private set; } = initial;

    /// <summary>Notes a frame's capture time.</summary>
    /// <param name="time">The time.</param>
    public void Observe(MediaTime time)
    {
        // A source that steps back (a restart, another clock) starts the window over.
        if (_times.Count > 0 && time <= _times.Last())
        {
            _times.Clear();
        }

        _times.Enqueue(time);
        while (time - _times.Peek() > Window)
        {
            _ = _times.Dequeue();
        }

        TimeSpan span = time - _times.Peek();
        if (_times.Count >= 10 && span >= Window * 0.9)
        {
            FramesPerSecond = (_times.Count - 1) / span.TotalSeconds;
        }
    }

    /// <summary>Whether a rate differs enough from an applied one to retune the encoder.</summary>
    /// <param name="applied">The rate the encoder plans for.</param>
    /// <param name="measured">The rate now.</param>
    /// <returns>Whether they differ by more than a fifth.</returns>
    public static bool Moved(double applied, double measured) =>
        measured < applied * 0.8 || measured > applied * 1.2;
}
