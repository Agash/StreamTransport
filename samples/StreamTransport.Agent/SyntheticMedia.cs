using System.Diagnostics;
using Agash.StreamTransport;

namespace StreamTransport.Agent;

/// <summary>
/// An animated NV12 test pattern, so the agent can publish without any capture hardware. A moving
/// vertical bar over a colour gradient gives the receiver something obviously live to verify. With
/// <paramref name="verify"/>, a sync marker carrying the <see cref="SyncMarkerCodec"/> payload is emitted at
/// each wall-clock second (a forced keyframe so the marker's data strip decodes cleanly).
/// </summary>
internal sealed class TestPatternVideoSource(int width, int height, int fps, bool alpha = false, bool verify = false) : IVideoFrameSource
{
    private readonly byte[] _nv12 = alpha ? [] : new byte[width * height * 3 / 2];
    private readonly byte[] _bgra = alpha ? new byte[width * height * 4] : [];
    private readonly long _ticksPerFrame = Stopwatch.Frequency / fps;
    private long _nextTick = Stopwatch.GetTimestamp();
    private int _frame;
    private int _lastMarkerSeq = -1;

    public bool TryGetFrame(out VideoFrame frame)
    {
        long now = Stopwatch.GetTimestamp();
        if (now < _nextTick)
        {
            frame = default;
            return false;
        }

        _nextTick += _ticksPerFrame;
        // Re-anchor if the pump fell more than a frame behind (likelier at high fps, where the 1/fps budget is
        // tight): otherwise the next calls emit a catch-up BURST with clustered capture timestamps, which the
        // receiver presents at its steady decode cadence - making the arrival offset (localNow - senderWall)
        // drift and breaking A/V sync at high fps. Drop the backlog and keep capture times evenly 1/fps apart,
        // like a real paced capture source.
        if (now - _nextTick > _ticksPerFrame)
        {
            _nextTick = now;
        }

        // Emit the marker on exactly ONE frame per wall second (the first frame of each new second), not for a
        // window of frames. A window would tag several frames with the same seqId, and the receiver could pair a
        // different one per stream - an ambiguity of up to the window width. One frame per second makes the
        // video/audio pairing unambiguous, so the measured skew is the true A/V offset.
        int seq = SyncMarkerCodec.CurrentSeqId();
        bool marker = verify && seq != _lastMarkerSeq;
        if (marker) { _lastMarkerSeq = seq; }

        // One capture timestamp for this frame, stamped at production and used for BOTH the frame's
        // PresentationTimeNs (which the transport turns into abs-capture-time) and the marker's embedded
        // captureMs, so the verify metric compares like with like. (Capturing it before rendering also means
        // the embedded time is not skewed by render cost.)
        long captureNs = NowNs();
        if (alpha)
        {
            // BGRA with a horizontal alpha gradient, so the transparency path is exercised end to end. The
            // data-strip marker codec is the NV12 path; the alpha path keeps the plain white marker.
            if (marker) { _bgra.AsSpan().Fill(255); } else { RenderBgra(_frame); } // white opaque marker
            _frame++;
            frame = VideoFrame.FromPixels(_bgra, VideoPixelFormat.Bgra, width, height, captureNs);
        }
        else
        {
            if (marker)
            {
                SyncMarkerCodec.RenderVideoMarker(_nv12, width, height, seq, SyncMarkerCodec.CaptureMsFromNs(captureNs));
                _nv12.AsSpan(width * height).Fill(128); // neutral chroma
            }
            else
            {
                Render(_frame);
            }

            _frame++;
            frame = VideoFrame.FromPixels(_nv12, VideoPixelFormat.Nv12, width, height, captureNs);
            // A marker frame is a forced keyframe so its luma data strip decodes losslessly enough to read back.
            if (marker)
            {
                frame = frame with { ForceKeyframe = true };
            }
        }

        return true;
    }

    private void RenderBgra(int frameIndex)
    {
        int barX = (frameIndex * 8) % width;
        for (int y = 0; y < height; y++)
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int p = row + (x * 4);
                byte r = (byte)((x + frameIndex) & 0xFF);
                byte g = (byte)(y * 255 / height);
                byte b = 128;
                byte a = (byte)(x * 255 / width); // horizontal transparency gradient
                if (Math.Abs(x - barX) < 6)
                {
                    r = g = b = 235;               // bright moving bar...
                    a = 255;                       // ...fully opaque.
                }

                _bgra[p] = b;
                _bgra[p + 1] = g;
                _bgra[p + 2] = r;
                _bgra[p + 3] = a;
            }
        }
    }

    private void Render(int frameIndex)
    {
        int barX = (frameIndex * 8) % width;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                // Luma: gradient with a bright moving bar.
                byte luma = (byte)(((x + frameIndex) & 0xFF));
                if (Math.Abs(x - barX) < 6)
                {
                    luma = 235;
                }

                _nv12[row + x] = luma;
            }
        }

        // Chroma plane (NV12 interleaved UV at half resolution): a slow colour sweep.
        int uvStart = width * height;
        int uvHeight = height / 2;
        int uvWidth = width / 2;
        for (int y = 0; y < uvHeight; y++)
        {
            int row = uvStart + (y * width);
            for (int x = 0; x < uvWidth; x++)
            {
                _nv12[row + (2 * x)] = (byte)(128 + (64 * Math.Sin(frameIndex * 0.05)));     // U
                _nv12[row + (2 * x) + 1] = (byte)(128 + (64 * Math.Cos(frameIndex * 0.05))); // V
            }
        }
    }

    private static long NowNs() => Stopwatch.GetTimestamp() * (1_000_000_000L / Stopwatch.Frequency);
}

/// <summary>
/// A 440 Hz sine tone in 16-bit mono at 48 kHz, paced in 20 ms frames. Guaranteed cross-platform (Opus is
/// managed). With <paramref name="verify"/>, a loud burst sync marker is emitted at each wall-clock second whose
/// frequency encodes the <see cref="SyncMarkerCodec"/> sequence id, so the receiver can pair it with the video
/// marker carrying the same id.
/// </summary>
internal sealed class SineToneAudioSource(bool verify = false) : IAudioFrameSource
{
    private const int SampleRate = 48_000;
    private const int SamplesPerFrame = SampleRate / 50; // 20 ms.
    private readonly long _ticksPerFrame = Stopwatch.Frequency / 50;
    private long _nextTick = Stopwatch.GetTimestamp();
    private long _sample;
    private int _lastMarkerSeq = -1;

    public bool TryGetFrame(out AudioFrame frame)
    {
        long now = Stopwatch.GetTimestamp();
        if (now < _nextTick)
        {
            frame = default;
            return false;
        }

        _nextTick += _ticksPerFrame;
        // Re-anchor if the pump fell more than a frame behind (likelier at high fps, where the 1/fps budget is
        // tight): otherwise the next calls emit a catch-up BURST with clustered capture timestamps, which the
        // receiver presents at its steady decode cadence - making the arrival offset (localNow - senderWall)
        // drift and breaking A/V sync at high fps. Drop the backlog and keep capture times evenly 1/fps apart,
        // like a real paced capture source.
        if (now - _nextTick > _ticksPerFrame)
        {
            _nextTick = now;
        }
        long captureNs = NowNs();
        // One marker burst per wall second (first audio frame of each new second), matching the video source -
        // see the note there; this keeps the A/V marker pairing unambiguous.
        int seq = SyncMarkerCodec.CurrentSeqId();
        bool marker = verify && seq != _lastMarkerSeq;
        if (marker) { _lastMarkerSeq = seq; }
        double amplitude = marker ? 0.95 : 0.3; // loud burst marker vs the steady tone
        double freq = marker ? SyncMarkerCodec.AudioFrequency(SyncMarkerCodec.CurrentSeqId()) : 440;
        byte[] pcm = new byte[SamplesPerFrame * 2];
        for (int i = 0; i < SamplesPerFrame; i++)
        {
            short value = (short)(short.MaxValue * amplitude * Math.Sin(2 * Math.PI * freq * _sample++ / SampleRate));
            pcm[2 * i] = (byte)(value & 0xFF);
            pcm[(2 * i) + 1] = (byte)((value >> 8) & 0xFF);
        }

        frame = new AudioFrame(pcm, AudioSampleFormat.S16, SampleRate, 1, captureNs);
        return true;
    }

    private static long NowNs() => Stopwatch.GetTimestamp() * (1_000_000_000L / Stopwatch.Frequency);
}

/// <summary>A video sink that just counts and periodically reports decoded frames.</summary>
internal sealed class ReportingVideoSink(Action<string> log) : IVideoFrameSink
{
    private int _count;

    public void Submit(VideoFrame frame)
    {
        int n = Interlocked.Increment(ref _count);
        if (n == 1 || n % 60 == 0)
        {
            log($"video: {n} frames ({frame.Width}x{frame.Height}, {(frame.InteropKind == StreamInteropKind.None ? "cpu" : "gpu")})");
        }
    }
}

/// <summary>An audio sink that counts decoded frames and reports periodically.</summary>
internal sealed class ReportingAudioSink(Action<string> log) : IAudioFrameSink
{
    private int _count;

    public void Submit(AudioFrame frame)
    {
        int n = Interlocked.Increment(ref _count);
        if (n == 1 || n % 100 == 0)
        {
            log($"audio: {n} frames ({frame.SampleRate} Hz, {frame.Channels}ch)");
        }
    }
}
