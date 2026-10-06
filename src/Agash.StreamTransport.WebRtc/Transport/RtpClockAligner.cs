using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.WebRtc.Transport;

/// <summary>
/// Maps a received stream's RTP timestamps to the sender's capture instants, from the abs-capture-time
/// the sender stamps on packets: the latest (capture, RTP timestamp) pair anchors the stream, and other
/// timestamps extrapolate from it at the stream's clock rate. Both streams of a session land on the
/// sender's one capture clock, which is what lip sync compares.
/// </summary>
internal sealed class RtpClockAligner(ClockRate rate)
{
    private readonly Lock _gate = new();
    private (NtpTime Capture, uint Timestamp)? _anchor;

    /// <summary>Whether a capture time has been seen.</summary>
    public bool IsAnchored
    {
        get
        {
            lock (_gate)
            {
                return _anchor is not null;
            }
        }
    }

    /// <summary>Records a packet's abs-capture-time and RTP timestamp.</summary>
    /// <param name="capture">The capture instant the packet carried.</param>
    /// <param name="timestamp">The packet's RTP timestamp.</param>
    public void Record(NtpTime capture, uint timestamp)
    {
        lock (_gate)
        {
            _anchor = (capture, timestamp);
        }
    }

    /// <summary>The sender's capture instant for an RTP timestamp; false before any anchor.</summary>
    /// <param name="timestamp">The RTP timestamp.</param>
    /// <param name="capture">The capture instant.</param>
    /// <returns>Whether the stream is anchored.</returns>
    public bool TryGetCapture(uint timestamp, out NtpTime capture)
    {
        (NtpTime Capture, uint Timestamp)? anchor;
        lock (_gate)
        {
            anchor = _anchor;
        }

        if (anchor is not { } a)
        {
            capture = default;
            return false;
        }

        // Signed, so a wrapped timestamp and a frame older than the anchor both come out right.
        int ticks = unchecked((int)(timestamp - a.Timestamp));
        capture = a.Capture + rate.ToTimeSpan(ticks);
        return true;
    }
}
