using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Sync;

/// <summary>
/// Measures lip sync at the point frames reach the sinks, in either playout mode. When a video frame is
/// presented, the audio heard at that moment is the last audio presented, advanced by the time since; the
/// difference of their capture instants, both on the sender's clock, is the A/V offset: positive when audio
/// lags video. Video presented with no audio in the last half second is not measured.
/// </summary>
/// <remarks>
/// The measurement is where the session hands frames over; what the sinks and the output devices add after
/// that is outside it, which <see cref="Playout.AudioOutputOffset"/> compensates for.
/// </remarks>
internal sealed class SyncMonitor(MediaClock clock, StreamTransportMetrics metrics)
{
    private static readonly TimeSpan AudioStale = TimeSpan.FromMilliseconds(500);

    // How much each new measurement moves the smoothed offset.
    private const double Smoothing = 1.0 / 16;

    private readonly Lock _gate = new();
    private NtpTime _audioCapture;
    private MediaTime _audioPresented;
    private bool _audioSeen;
    private double? _smoothedTicks;

    /// <summary>The smoothed A/V offset; null until audio and video have both been presented.</summary>
    public TimeSpan? Offset
    {
        get
        {
            lock (_gate)
            {
                return _smoothedTicks is { } ticks ? TimeSpan.FromTicks((long)ticks) : null;
            }
        }
    }

    public void AudioPresented(NtpTime capture)
    {
        MediaTime now = clock.Now;
        lock (_gate)
        {
            _audioCapture = capture;
            _audioPresented = now;
            _audioSeen = true;
        }
    }

    public void VideoPresented(NtpTime capture)
    {
        MediaTime now = clock.Now;
        TimeSpan offset;
        lock (_gate)
        {
            TimeSpan sinceAudio = now - _audioPresented;
            if (!_audioSeen || sinceAudio > AudioStale)
            {
                return;
            }

            // The audio heard now was captured this long after the last audio presented was.
            long heardNanoseconds = _audioCapture.Nanoseconds + (sinceAudio.Ticks * 100);
            offset = TimeSpan.FromTicks((capture.Nanoseconds - heardNanoseconds) / 100);
            _smoothedTicks = _smoothedTicks is { } previous
                ? previous + (Smoothing * (offset.Ticks - previous))
                : offset.Ticks;
        }

        metrics.AvSyncOffset.Record(offset.TotalSeconds);
    }
}
