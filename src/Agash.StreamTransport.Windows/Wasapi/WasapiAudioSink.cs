using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32.Media.Audio;

namespace Agash.StreamTransport.Windows.Wasapi;

/// <summary>
/// Plays audio on the default output device through WASAPI. It takes 48 kHz stereo float, which WASAPI
/// converts to the device's format. Audio waits in a short buffer for the device; when the device runs
/// dry it plays silence, and when audio arrives faster than it plays the oldest is dropped, so output
/// stays near real time.
/// </summary>
public sealed partial class WasapiAudioSink : IAudioSink, IDisposable
{
    // Two hundred milliseconds of stereo: jitter the playout buffer did not absorb.
    private const int BufferedSamples = 48_000 / 5 * 2;

    private readonly FloatRing _ring = new(BufferedSamples);
    private readonly ILogger _logger;
    private readonly ManualResetEvent _stop = new(false);
    private readonly TaskCompletionSource<TimeSpan> _started = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly Thread _thread;
    private int _disposed;

    /// <summary>A sink on the default output device.</summary>
    /// <param name="loggerFactory">Where the sink logs.</param>
    public WasapiAudioSink(ILoggerFactory? loggerFactory = null)
    {
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WasapiAudioSink>();
        _thread = new Thread(Run) { IsBackground = true, Name = "WASAPI render" };
        _thread.Start();
    }

    /// <inheritdoc/>
    public AudioConstraints Constraints { get; } = new([48_000], [SampleFormat.F32], 2);

    /// <summary>
    /// How long audio takes from this sink to the speaker, once the device has started: what a session's
    /// audio output offset is measured against.
    /// </summary>
    public Task<TimeSpan> OutputLatency => _started.Task;

    /// <inheritdoc/>
    public void OnFrame(in AudioFrame frame)
    {
        if (frame.Format != WasapiClient.Format)
        {
            throw new ArgumentException(
                $"The sink takes {WasapiClient.Format}; the audio is {frame.Format}.",
                nameof(frame)
            );
        }

        _ring.Write(MemoryMarshal.Cast<byte, float>(frame.Samples));
    }

    /// <summary>Stops playing and releases the device.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _ = _stop.Set();
            _thread.Join();
            _stop.Dispose();
        }
    }

    private unsafe void Run()
    {
        try
        {
            using WasapiClient client = new(WasapiEndpoint.DefaultOutput);
            IAudioRenderClient* render = client.Service<IAudioRenderClient>();
            try
            {
                Fill(client, render);
                client.Start();
                _ = _started.TrySetResult(client.Latency);
                WaitHandle[] waits = [client.Ready, _stop];
                while (WaitHandle.WaitAny(waits) == 0)
                {
                    Fill(client, render);
                }
            }
            finally
            {
                _ = render->Release();
            }
        }
        catch (COMException exception)
        {
            // The device went away or refused the stream; audio stops playing.
            LogRenderFailed(exception);
            _ = _started.TrySetException(exception);
        }
    }

    // Fills the room in the device buffer: queued audio first, then silence.
    private unsafe void Fill(WasapiClient client, IAudioRenderClient* render)
    {
        uint frames = client.BufferFrames - client.Padding;
        if (frames == 0)
        {
            return;
        }

        byte* data;
        render->GetBuffer(frames, &data);
        Span<float> samples = new(data, (int)frames * 2);
        int read = _ring.Read(samples);
        samples[read..].Clear();
        render->ReleaseBuffer(frames, 0);
    }

    [LoggerMessage(2330, LogLevel.Error, "Playing audio stopped with an error.")]
    private partial void LogRenderFailed(Exception exception);
}
