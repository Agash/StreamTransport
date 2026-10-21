using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Sync;

/// <summary>
/// Stamps received frames with the local time they arrived, strictly increasing so every frame's stamp
/// is its own, and remembers the sender's capture instant for each stamp. Decoders carry a frame's stamp
/// through, so a decoded frame's stamp finds its capture instant again for playout.
/// </summary>
internal sealed class ArrivalStamps(MediaClock clock)
{
    // Stamps whose frames a decoder dropped are forgotten once this many newer ones are waiting.
    private const int Remembered = 512;

    private readonly Lock _gate = new();
    private readonly Dictionary<MediaTime, NtpTime> _captures = [];
    private readonly Queue<MediaTime> _order = new();
    private MediaTime _last;

    /// <summary>The stamp for a frame arriving now.</summary>
    /// <param name="capture">The sender's capture instant, when known.</param>
    /// <returns>The frame's timestamp: an observation of its arrival.</returns>
    public MediaTimestamp Stamp(NtpTime? capture)
    {
        lock (_gate)
        {
            MediaTime now = clock.Now;
            _last = now > _last ? now : new MediaTime(_last.Nanoseconds + 1);
            if (capture is { } at)
            {
                _captures[_last] = at;
                _order.Enqueue(_last);
                while (_order.Count > Remembered)
                {
                    _ = _captures.Remove(_order.Dequeue());
                }
            }

            return MediaTimestamp.Observed(_last);
        }
    }

    /// <summary>The capture instant of a stamped frame, forgotten once taken.</summary>
    /// <param name="stamp">The frame's stamp.</param>
    /// <returns>The capture instant, or null when unknown.</returns>
    public NtpTime? TakeCapture(MediaTimestamp stamp)
    {
        lock (_gate)
        {
            return _captures.Remove(stamp.Time, out NtpTime capture) ? capture : null;
        }
    }
}
