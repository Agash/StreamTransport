namespace Agash.StreamTransport.Sync;

/// <summary>The two media streams whose clocks are aligned onto the sender's wall clock.</summary>
internal enum SyncStream
{
    Audio,
    Video,
}

/// <summary>
/// Aligns the audio and video RTP clocks onto the sender's single NTP wall clock, so audio and video can be
/// lip-synced. Two sources of (NTP wall time, RTP timestamp) anchors are supported, in priority
/// order:
/// <list type="number">
/// <item><b>abs-capture-time</b> RTP header extension - the sender stamps each packet with the absolute NTP
/// capture time of its frame, so the anchor is the true capture instant, independent of RTCP timing. This is
/// the libwebrtc-correct basis for cross-stream sync and is preferred when present.</item>
/// <item><b>RTCP Sender Reports</b> - each SR carries a (NTP, RTP) pair; usable but its NTP corresponds to the
/// report-send instant, which carries each stream's send-path latency and so can leave a constant cross-stream
/// offset. Used only as a fallback until abs-capture-time is seen.</item>
/// </list>
/// Mapping a stream's RTP timestamp through its anchor (extrapolating with the media clock rate) puts both
/// streams on one comparable capture timeline. Pure and thread-safe; holds only the latest anchor per stream.
/// </summary>
internal sealed class RtpClockAligner
{
    private readonly Lock _gate = new();
    private Anchor? _audioSr;
    private Anchor? _videoSr;
    private Anchor? _audioAct;
    private Anchor? _videoAct;

    /// <summary>True once both streams can be mapped (each has an abs-capture-time or Sender Report anchor).</summary>
    public bool BothAligned
    {
        get
        {
            lock (_gate)
            {
                return (_audioAct ?? _audioSr) is not null && (_videoAct ?? _videoSr) is not null;
            }
        }
    }

    /// <summary>Record a stream's latest RTCP Sender Report (NTP wall time it was emitted, RTP timestamp at that instant).</summary>
    public void RecordSenderReport(SyncStream stream, ulong ntpTimestamp, uint rtpTimestamp, int clockRate)
    {
        var anchor = new Anchor(NtpToNs(ntpTimestamp), rtpTimestamp, clockRate);
        lock (_gate)
        {
            if (stream == SyncStream.Audio)
            {
                _audioSr = anchor;
            }
            else
            {
                _videoSr = anchor;
            }
        }
    }

    /// <summary>
    /// Record an abs-capture-time observation: the absolute NTP capture time carried by a packet and that
    /// packet's RTP timestamp. Preferred over Sender Reports for mapping this stream's RTP onto wall time.
    /// </summary>
    public void RecordAbsCaptureTime(SyncStream stream, ulong captureNtp, uint rtpTimestamp, int clockRate)
    {
        var anchor = new Anchor(NtpToNs(captureNtp), rtpTimestamp, clockRate);
        lock (_gate)
        {
            if (stream == SyncStream.Audio)
            {
                _audioAct = anchor;
            }
            else
            {
                _videoAct = anchor;
            }
        }
    }

    /// <summary>
    /// Map an RTP timestamp on <paramref name="stream"/> to the sender's wall-clock time in nanoseconds, using
    /// that stream's best anchor (abs-capture-time if seen, else its Sender Report). Returns false until an
    /// anchor exists. Handles 32-bit RTP timestamp wraparound (the delta is interpreted signed).
    /// </summary>
    public bool TryToSenderWallNs(SyncStream stream, uint rtpTimestamp, out long wallNs)
    {
        Anchor? anchor;
        lock (_gate)
        {
            anchor = stream == SyncStream.Audio ? _audioAct ?? _audioSr : _videoAct ?? _videoSr;
        }

        if (anchor is not { } a)
        {
            wallNs = 0;
            return false;
        }

        // Signed delta so a wrapped 32-bit RTP timestamp still yields the correct (small) interval, and so a
        // frame earlier than the latest anchor (e.g. a decoder's one-frame pipeline lag) extrapolates back.
        int deltaRtp = unchecked((int)(rtpTimestamp - a.RtpTimestamp));
        long deltaNs = (long)deltaRtp * 1_000_000_000L / a.ClockRate;
        wallNs = a.NtpNs + deltaNs;
        return true;
    }

    /// <summary>Convert a 64-bit NTP timestamp (seconds.fraction since 1900) to nanoseconds since the NTP epoch.</summary>
    private static long NtpToNs(ulong ntp)
    {
        ulong seconds = ntp >> 32;
        ulong fraction = ntp & 0xFFFFFFFF;
        return (long)(seconds * 1_000_000_000UL) + (long)((fraction * 1_000_000_000UL) >> 32);
    }

    private readonly record struct Anchor(long NtpNs, uint RtpTimestamp, int ClockRate);
}
