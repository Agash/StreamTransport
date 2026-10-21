using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Streams;

/// <summary>
/// Chooses a send stream's timing frames and times their encoding, as libwebrtc's
/// FrameEncodeMetadataWriter does: a frame is timed when 200 ms of capture time have passed since the last
/// timed one, or when it is five times the average frame size for the target rate or more. Encode start is
/// recorded per frame as it goes in, since an encoder may hand frames back later and in a different call;
/// at most 150 starts wait for their frames.
/// </summary>
internal sealed class FrameTimingTracker
{
    private static readonly TimeSpan TimerInterval = TimeSpan.FromMilliseconds(200);
    private const int OutlierPercent = 500;
    private const int MaxPending = 150;

    private readonly Lock _gate = new();
    private readonly Dictionary<MediaTime, MediaTime> _starts = [];
    private readonly Queue<MediaTime> _order = new();
    private MediaTime? _lastTimed;

    /// <summary>The encoder took the frame captured at <paramref name="capture"/>.</summary>
    /// <param name="capture">The frame's capture instant.</param>
    /// <param name="now">The time.</param>
    public void EncodeStarted(MediaTime capture, MediaTime now)
    {
        lock (_gate)
        {
            if (_starts.TryAdd(capture, now))
            {
                _order.Enqueue(capture);
            }

            while (_order.Count > MaxPending && _order.TryDequeue(out MediaTime oldest))
            {
                _ = _starts.Remove(oldest);
            }
        }
    }

    /// <summary>
    /// The encoder produced the frame captured at <paramref name="capture"/>: its timing, when it is a timing
    /// frame and its start was recorded.
    /// </summary>
    /// <param name="capture">The frame's capture instant.</param>
    /// <param name="bytes">The encoded frame's size.</param>
    /// <param name="bitsPerSecond">The stream's target rate.</param>
    /// <param name="framesPerSecond">The stream's frame rate.</param>
    /// <param name="now">The time.</param>
    /// <returns>The timing, or null.</returns>
    public FrameSendTiming? EncodeFinished(
        MediaTime capture,
        int bytes,
        long bitsPerSecond,
        double framesPerSecond,
        MediaTime now
    )
    {
        lock (_gate)
        {
            if (!_starts.Remove(capture, out MediaTime started))
            {
                return null;
            }

            FrameTimingReasons reasons = FrameTimingReasons.None;
            if (_lastTimed is not { } last || capture - last >= TimerInterval)
            {
                reasons |= FrameTimingReasons.Timer;
                _lastTimed = capture;
            }

            if (
                framesPerSecond > 0
                && bitsPerSecond > 0
                && bytes * 100.0 >= bitsPerSecond / 8.0 / framesPerSecond * OutlierPercent
            )
            {
                reasons |= FrameTimingReasons.Size;
            }

            return reasons == FrameTimingReasons.None
                ? null
                : new FrameSendTiming(started - capture, now - capture, reasons);
        }
    }
}
