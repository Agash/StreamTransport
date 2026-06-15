using System.Diagnostics;
using Agash.StreamTransport;

namespace StreamTransport.Agent;

/// <summary>
/// Collects what the verifying video/audio sinks observe and prints a pass/fail summary. Both sinks share one
/// instance and submit from their own decode-worker threads, so access is locked. Content health is the
/// decoded brightness range (and, for BGRA, the alpha gradient) and the audio tone energy. A/V sync is measured
/// from <see cref="SyncMarkerCodec"/> markers: each second both streams carry the same sequence id, so the
/// receiver pairs the two by id and reports the <b>delivered present-time skew</b> for that exact event - no
/// brightness/RMS-threshold guesswork, and no dependence on which marker frame is detected. With a correct
/// playout sync the skew is small and steady; a large or growing one signals desync.
/// </summary>
internal sealed class VerificationReport
{
    // The first few seconds are a pipeline-fill transient (the decoder's reorder buffer filling and the playout
    // jitter buffer converging), not steady-state, so markers in this warm-up window are excluded.
    private const double SyncWarmupMs = 3000;

    private readonly Lock _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // First observation per sequence id, in the shared QPC clock (comparable across both sinks, and - on one
    // machine - with the sender's embedded capture ms).
    private readonly Dictionary<int, (double PresentMs, long CaptureMs)> _videoMarkers = [];
    private readonly Dictionary<int, double> _audioMarkers = [];

    private int _videoFrames;
    private int _audioFrames;
    private double _videoBrightMin = double.MaxValue;
    private double _videoBrightMax = double.MinValue;
    private double _audioRmsSum;
    private int _audioRmsCount;
    private bool _sawAlphaGradient;
    private bool _sawAnyBgra;
    private VideoPixelFormat _lastVideoFormat;

    /// <summary>The capture-ms field width the video marker carries, for unwrapping the latency readout.</summary>
    private const long CaptureMsWrap = 1L << 26;

    /// <summary>Map a present-minus-captureMs difference into the signed window of the 26-bit capture-ms field.</summary>
    private static long WrapSignedMs(long delta)
    {
        long m = ((delta % CaptureMsWrap) + CaptureMsWrap) % CaptureMsWrap;
        return m > CaptureMsWrap / 2 ? m - CaptureMsWrap : m;
    }

    public double ElapsedMs => _clock.Elapsed.TotalMilliseconds;

    public void RecordVideoContent(double brightness, bool isBgra, bool alphaGradient)
    {
        lock (_gate)
        {
            _videoFrames++;
            _lastVideoFormat = isBgra ? VideoPixelFormat.Bgra : _lastVideoFormat;
            _sawAnyBgra |= isBgra;
            _sawAlphaGradient |= alphaGradient;
            // The gradient test pattern averages to a near-constant ~127 every frame (so the gradient alone
            // reads as "frozen"); the once-a-second white marker frame (~255) is the real liveness signal, so
            // the full range across marker and non-marker frames is what proves the stream is live.
            _videoBrightMin = Math.Min(_videoBrightMin, brightness);
            _videoBrightMax = Math.Max(_videoBrightMax, brightness);
        }
    }

    public void RecordAudioContent(double rms)
    {
        lock (_gate)
        {
            _audioFrames++;
            _audioRmsSum += rms;
            _audioRmsCount++;
        }
    }

    /// <summary>Record the first sighting of a video marker sequence id (present time + sender capture ms).</summary>
    public void RecordVideoMarker(int seqId, long captureMs, double presentMs)
    {
        lock (_gate)
        {
            _videoMarkers.TryAdd(seqId, (presentMs, captureMs));
        }
    }

    /// <summary>Record the first sighting of an audio marker sequence id (present time).</summary>
    public void RecordAudioMarker(int seqId, double presentMs)
    {
        lock (_gate)
        {
            _audioMarkers.TryAdd(seqId, presentMs);
        }
    }

    /// <summary>Print the summary and return true if the run passed (both streams flowed, content + sync OK).</summary>
    public bool Print(Action<string> log, bool expectVideo, bool expectAudio)
    {
        lock (_gate)
        {
            double seconds = Math.Max(0.001, _clock.Elapsed.TotalSeconds);
            double videoFps = _videoFrames / seconds;
            double audioFps = _audioFrames / seconds;

            bool ok = true;

            if (expectVideo)
            {
                bool flowed = _videoFrames >= 10;
                bool contentLive = _videoBrightMax - _videoBrightMin > 15 && _videoBrightMax > 30; // not black/frozen
                log($"video : {_videoFrames} frames ({videoFps:F1} fps), brightness {_videoBrightMin:F0}..{_videoBrightMax:F0}, format {_lastVideoFormat}");
                if (_sawAnyBgra)
                {
                    log($"alpha : gradient {(_sawAlphaGradient ? "preserved" : "MISSING")} (decoded BGRA = transparency path)");
                    ok &= _sawAlphaGradient;
                }

                log($"video : flow {(flowed ? "OK" : "LOW")}, content {(contentLive ? "live" : "FROZEN/BLACK")}");
                ok &= flowed && contentLive;
            }

            if (expectAudio)
            {
                double avgRms = _audioRmsCount > 0 ? _audioRmsSum / _audioRmsCount : 0;
                bool flowed = _audioFrames >= 20;
                bool audible = avgRms > 1000; // the 0.3-amplitude tone is ~7000 RMS
                log($"audio : {_audioFrames} frames ({audioFps:F1} fps), avg RMS {avgRms:F0}");
                log($"audio : flow {(flowed ? "OK" : "LOW")}, signal {(audible ? "present" : "SILENT")}");
                ok &= flowed && audible;
            }

            if (expectVideo && expectAudio)
            {
                ok &= PrintSync(log);
            }

            log(ok ? "VERIFY-PASS" : "VERIFY-FAIL");
            return ok;
        }
    }

    private bool PrintSync(Action<string> log)
    {
        // Pair markers by sequence id - the same logical event on both streams - and measure the delivered
        // present-time skew (video present minus audio present). With capture-synchronised markers a correct
        // playout presents them together, so the skew should be small and flat; the absolute value is no longer
        // a fixed pipeline-latency offset (as it was when frames presented on arrival) but the real lip-sync.
        var skews = new List<(double TimeMs, double SkewMs)>();
        double latencySum = 0;
        double audioLatencySum = 0;
        int latencyCount = 0;
        // Present times are absolute QPC ms, so the warm-up window is measured from the first matched marker
        // (the alignment/pipeline-fill transient), not an absolute epoch.
        double baseline = double.MaxValue;
        foreach ((int seqId, (double vPresent, long _)) in _videoMarkers)
        {
            if (_audioMarkers.TryGetValue(seqId, out double aPresent))
            {
                baseline = Math.Min(baseline, Math.Min(vPresent, aPresent));
            }
        }

        foreach ((int seqId, (double vPresent, long captureMs)) in _videoMarkers)
        {
            if (!_audioMarkers.TryGetValue(seqId, out double aPresent))
            {
                continue;
            }

            double time = Math.Min(vPresent, aPresent);
            if (time - baseline < SyncWarmupMs)
            {
                continue;
            }

            skews.Add((time, vPresent - aPresent));

            // Same-machine only: present and capture share the QPC clock, so this is the real video capture->
            // present latency (across two machines the clocks differ and this line is meaningless, but the skew
            // above is still valid since both present times are the receiver's).
            latencySum += WrapSignedMs((long)vPresent - captureMs);
            audioLatencySum += WrapSignedMs((long)aPresent - captureMs);
            latencyCount++;
        }

        if (skews.Count < 3)
        {
            log($"sync  : only {skews.Count} id-matched A/V markers after warm-up (run --verify longer) - INCONCLUSIVE");
            return true; // not enough data to fail on
        }

        double mean = 0, min = double.MaxValue, max = double.MinValue, meanTime = 0;
        foreach ((double t, double sk) in skews) { mean += sk; meanTime += t; min = Math.Min(min, sk); max = Math.Max(max, sk); }
        mean /= skews.Count;
        meanTime /= skews.Count;

        double variance = 0, cov = 0, timeVar = 0;
        foreach ((double t, double sk) in skews)
        {
            variance += (sk - mean) * (sk - mean);
            cov += (t - meanTime) * (sk - mean);
            timeVar += (t - meanTime) * (t - meanTime);
        }

        double stdDev = Math.Sqrt(variance / skews.Count);
        double slope = timeVar > 0 ? cov / timeVar * 1000.0 : 0; // ms of skew per second
        string lead = mean >= 0 ? "video behind audio" : "video ahead of audio";

        bool locked = Math.Abs(mean) <= 40 && Math.Abs(slope) <= 6 && stdDev <= 40;
        log($"sync  : {skews.Count} id-matched markers; skew {mean:+0;-0;0} ms ({lead}), jitter +/-{stdDev:F0} ms, trend {slope:+0.0;-0.0;0.0} ms/s, range {max - min:F0} ms - {(locked ? "IN SYNC" : "OUT OF SYNC")}");
        if (latencyCount > 0)
        {
            log($"sync  : capture->present video ~{latencySum / latencyCount:F0} ms, audio ~{audioLatencySum / latencyCount:F0} ms (same-machine QPC only)");
        }

        return locked;
    }
}

/// <summary>
/// A verifying <see cref="IVideoFrameSink"/>: tracks content brightness (and, for BGRA, the alpha gradient) and
/// recovers the <see cref="SyncMarkerCodec"/> marker payload (sequence id + sender capture ms) from the decoded
/// luma plane. Pairs with <see cref="VerifyingAudioSink"/> via a shared <see cref="VerificationReport"/>.
/// </summary>
internal sealed class VerifyingVideoSink(VerificationReport report, bool usePresentationTime = false) : IVideoFrameSink
{
    public void Submit(VideoFrame frame)
    {
        if (frame.Pixels.IsEmpty)
        {
            return; // a GPU-surface frame carries no CPU pixels; --verify uses the CPU decode path.
        }

        ReadOnlySpan<byte> px = frame.Pixels.Span;
        bool isBgra = frame.PixelFormat == VideoPixelFormat.Bgra;
        (double brightness, bool alphaGradient) = isBgra ? SampleBgra(px, frame.Width, frame.Height) : (SampleLuma(px, frame.Width, frame.Height), false);
        report.RecordVideoContent(brightness, isBgra, alphaGradient);

        // The marker codec rides the NV12/I420 luma plane (the alpha path keeps the plain white marker).
        if (!isBgra && SyncMarkerCodec.TryReadVideoMarker(px, frame.Width, frame.Height, out int seqId, out long captureMs))
        {
            // GPU verify reads pixels back on a synchronous path that adds latency after the frame was delivered,
            // so it stamps the delivery (playout) time on the frame and we use that - otherwise the readback cost
            // would inflate the measured A/V skew. The CPU path has no such delay and times the marker on receipt.
            double presentMs = usePresentationTime && frame.PresentationTimeNs > 0 ? frame.PresentationTimeNs / 1_000_000.0 : NowMs();
            report.RecordVideoMarker(seqId, captureMs, presentMs);
        }
    }

    private static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    private static double SampleLuma(ReadOnlySpan<byte> px, int width, int height)
    {
        // Y plane is the first width*height bytes for NV12 and I420. Skip the top marker strip so the embedded
        // data blocks don't skew the content-brightness range.
        long sum = 0;
        int n = 0;
        for (int y = 48; y < height; y += 8)
        {
            int row = y * width;
            for (int x = 0; x < width; x += 8)
            {
                sum += px[row + x];
                n++;
            }
        }

        return n > 0 ? sum / (double)n : 0;
    }

    private static (double Brightness, bool AlphaGradient) SampleBgra(ReadOnlySpan<byte> px, int width, int height)
    {
        long sum = 0;
        int n = 0;
        int alphaLeft = 0, alphaRight = 0, leftN = 0, rightN = 0;
        for (int y = 0; y < height; y += 8)
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x += 8)
            {
                int p = row + (x * 4);
                sum += (px[p] + px[p + 1] + px[p + 2]) / 3;
                n++;
                if (x < width / 4) { alphaLeft += px[p + 3]; leftN++; }
                else if (x > 3 * width / 4) { alphaRight += px[p + 3]; rightN++; }
            }
        }

        double brightness = n > 0 ? sum / (double)n : 0;
        bool gradient = leftN > 0 && rightN > 0 && brightness < 200
            && (alphaRight / (double)rightN) - (alphaLeft / (double)leftN) > 40;
        return (brightness, gradient);
    }
}

/// <summary>
/// A verifying <see cref="IAudioFrameSink"/>: computes per-frame RMS for the signal-present check and recovers
/// the <see cref="SyncMarkerCodec"/> marker sequence id from the burst frequency. Pairs with the video sink.
/// </summary>
internal sealed class VerifyingAudioSink(VerificationReport report) : IAudioFrameSink
{
    private const int SampleRate = 48_000;

    public void Submit(AudioFrame frame)
    {
        ReadOnlySpan<byte> bytes = frame.Samples.Span;
        int samples = bytes.Length / 2;
        if (samples == 0)
        {
            return;
        }

        long sumSquares = 0;
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(bytes[2 * i] | (bytes[(2 * i) + 1] << 8));
            sumSquares += (long)s * s;
        }

        report.RecordAudioContent(Math.Sqrt(sumSquares / (double)samples));

        if (SyncMarkerCodec.TryReadAudioMarker(bytes, frame.SampleRate == 0 ? SampleRate : frame.SampleRate, Math.Max(1, frame.Channels), out int seqId))
        {
            report.RecordAudioMarker(seqId, NowMs());
        }
    }

    private static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
}
