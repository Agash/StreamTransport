#if WINDOWS_HEAD
using System.Runtime.Versioning;
using Agash.StreamTransport;
using NAudio.Wave;
using AudioFrame = Agash.StreamTransport.AudioFrame;
using AudioSampleFormat = Agash.StreamTransport.AudioSampleFormat;

namespace StreamTransport.Agent;

/// <summary>
/// Windows audio playout (the Windows companion to <see cref="PipeWireAudioPublishSink"/>): renders decoded PCM
/// to the default WASAPI render device via NAudio, so OBS (Desktop / Application audio capture) and the rest of
/// the system hear it. WASAPI's render thread <i>pulls</i> through <see cref="IWaveProvider.Read"/>, which drains
/// the shared <see cref="PullAudioRingBuffer"/> (bounded backlog; underrun fills silence to keep the clock).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WasapiAudioPublishSink : IAudioFrameSink, IDisposable
{
    private const int MaxQueuedBytes = 192_000 / 5; // ~200 ms at 48 kHz stereo S16.

    private readonly PullAudioRingBuffer _ring = new(MaxQueuedBytes);
    private readonly Lock _gate = new();
    private IWavePlayer? _out;
    private bool _disposed;

    public void Submit(AudioFrame frame)
    {
        ReadOnlySpan<byte> samples = frame.Samples.Span;
        if (samples.IsEmpty || frame.Format != AudioSampleFormat.S16)
        {
            return; // the decoder emits interleaved S16; anything else would need conversion first.
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Lazy init on the first frame, when rate/channels are known. Shared-mode, ~50 ms device buffer.
            // NAudio 3's WasapiPlayer (GeneratedComInterface/LibraryImport interop) is NativeAOT-compatible,
            // unlike the legacy WasapiOut's classic-COM device enumeration.
            if (_out is null)
            {
                var format = new WaveFormat(frame.SampleRate, 16, Math.Max(1, frame.Channels));
                IWavePlayer output = new WasapiPlayerBuilder()
                    .WithSharedMode()
                    .WithEventSync()
                    .WithLatency(50)
                    .Build();
                output.Init(new RingWaveProvider(_ring, format));
                output.Play();
                _out = output;
            }
        }

        _ring.Write(samples);
    }

    public void Dispose()
    {
        IWavePlayer? output;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            output = _out;
            _out = null;
            _ring.Clear();
        }

        output?.Stop();
        output?.Dispose();
    }

    // Pull adapter: WASAPI asks for `count` bytes; drain the ring and zero-fill any shortfall so playback stays
    // continuous (returning < count would stop the stream). Always reports the full count as produced.
    private sealed class RingWaveProvider(PullAudioRingBuffer ring, WaveFormat format) : IWaveProvider
    {
        public WaveFormat WaveFormat => format;

        // NAudio 3 modernized IWaveProvider.Read to a single Span<byte> (no array/offset/count).
        public int Read(Span<byte> buffer)
        {
            int written = ring.Read(buffer);
            if (written < buffer.Length)
            {
                buffer[written..].Clear();
            }

            return buffer.Length;
        }
    }
}

/// <summary>
/// Windows audio capture for the sender: the default microphone (<see cref="WasapiCapture"/>) or the system
/// output loopback (<see cref="WasapiLoopbackCapture"/>, i.e. "what you hear" - the desktop-audio source for the
/// Spout GPU scenario). Captured frames are converted to 48 kHz S16 and pulled by the engine via
/// <see cref="TryGetFrame"/>. NAudio raises <c>DataAvailable</c> on its own thread; we queue and hand off.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WasapiAudioCaptureSource : IAudioFrameSource, IDisposable
{
    private readonly WasapiRecorder _capture;
    private readonly Lock _gate = new();
    private readonly Queue<AudioFrame> _frames = new();
    private readonly int _channels;
    private readonly int _sampleRate;
    private bool _disposed;

    /// <param name="loopback">true = capture system output ("what you hear"); false = the default microphone.</param>
    public WasapiAudioCaptureSource(bool loopback)
    {
        // NAudio 3's WasapiRecorder (GeneratedComInterface/LibraryImport interop) is NativeAOT-compatible.
        WasapiRecorderBuilder builder = new();
        if (loopback)
        {
            builder = builder.WithLoopbackCapture();
        }

        _capture = builder.Build();
        _channels = _capture.WaveFormat.Channels;
        _sampleRate = _capture.WaveFormat.SampleRate;
        _capture.DataAvailable += (buffer, _) => HandleCapturedData(buffer);
        _capture.StartRecording();
    }

    public bool TryGetFrame(out AudioFrame frame)
    {
        lock (_gate)
        {
            if (_frames.Count > 0)
            {
                frame = _frames.Dequeue();
                return true;
            }
        }

        frame = default;
        return false;
    }

    // WASAPI delivers 32-bit float; convert to interleaved S16 at the device rate (the Opus encoder resamples
    // /matches channel count downstream). Stamp the capture time so A/V sync has a reference. NAudio 3 hands the
    // captured bytes directly as a span (no WaveInEventArgs / byte[] copy).
    private void HandleCapturedData(ReadOnlySpan<byte> buffer)
    {
        int floatCount = buffer.Length / 4;
        if (floatCount == 0)
        {
            return;
        }

        byte[] pcm = new byte[floatCount * 2];
        Span<short> dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm);
        ReadOnlySpan<float> src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer);
        for (int i = 0; i < floatCount; i++)
        {
            dst[i] = (short)Math.Clamp(src[i] * 32767f, short.MinValue, short.MaxValue);
        }

        var frame = new AudioFrame(pcm, AudioSampleFormat.S16, _sampleRate, _channels, NowNs());
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _frames.Enqueue(frame);
            while (_frames.Count > 50) // ~1 s backlog cap; drop oldest if the encoder falls behind.
            {
                _frames.Dequeue();
            }
        }
    }

    private static long NowNs() => System.Diagnostics.Stopwatch.GetTimestamp() * (1_000_000_000L / System.Diagnostics.Stopwatch.Frequency);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        try { _capture.StopRecording(); } catch { /* already stopped */ }
        _capture.Dispose(); // releases the DataAvailable handler with the recorder.
    }
}
#endif
