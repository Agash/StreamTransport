using System.Runtime.InteropServices;
using Agash.StreamTransport.Audio;
using Agash.StreamTransport.Media;
using AVFoundation;
using Foundation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AudioFormat = Agash.StreamTransport.Media.AudioFormat;

namespace Agash.StreamTransport.MacOS.Audio;

/// <summary>
/// Plays audio on the default output device through Core Audio (AVAudioEngine). It takes 48 kHz stereo
/// float, which the engine converts to the device's format. Audio waits in a short buffer for the device;
/// when the device runs dry it plays silence, and when audio arrives faster than it plays the oldest is
/// dropped, so output stays near real time. The engine follows the default output when it changes.
/// </summary>
public sealed partial class CoreAudioSink : IAudioSink, IDisposable
{
    /// <summary>The format the sink takes.</summary>
    public static readonly AudioFormat Format = new(SampleFormat.F32, 48_000, 2);

    // Two hundred milliseconds of stereo: jitter the playout buffer did not absorb.
    private const int BufferedSamples = 48_000 / 5 * 2;

    // What the render thread reads from the ring before splitting it into channels: a whole render
    // quantum of stereo at the engine's largest.
    private const int ScratchSamples = 4096 * 2;

    private readonly FloatRing _ring = new(BufferedSamples);
    private readonly float[] _scratch = new float[ScratchSamples];
    private readonly AVAudioEngine _engine = new();
    private readonly AVAudioSourceNode _node;
    private readonly NSObject _changes;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>A sink on the default output device, playing from when it is made.</summary>
    /// <param name="loggerFactory">Where the sink logs.</param>
    /// <exception cref="InvalidOperationException">The output device refused to start.</exception>
    public CoreAudioSink(ILoggerFactory? loggerFactory = null)
    {
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<CoreAudioSink>();
        // The mixer takes the standard format, a float buffer per channel.
        using AVAudioFormat format = new(Format.SampleRate, 2);
        _node = new AVAudioSourceNode(format, new AVAudioSourceNodeRenderHandlerRaw(Render));
        _engine.AttachNode(_node);
        _engine.Connect(_node, _engine.MainMixerNode, format);
        _changes = NSNotificationCenter.DefaultCenter.AddObserver(
            AVAudioEngine.ConfigurationChangeNotification,
            _ => Restart(),
            _engine
        );
        Start();
    }

    /// <inheritdoc/>
    public AudioConstraints Constraints { get; } = new([48_000], [SampleFormat.F32], 2);

    /// <summary>
    /// How long audio takes from this sink to the speaker: what a session's audio output offset is
    /// measured against.
    /// </summary>
    public TimeSpan OutputLatency =>
        TimeSpan.FromSeconds(_engine.OutputNode.PresentationLatency + _engine.OutputNode.Latency);

    /// <inheritdoc/>
    public void OnFrame(in AudioFrame frame)
    {
        if (frame.Format != Format)
        {
            throw new ArgumentException(
                $"The sink takes {Format}; the audio is {frame.Format}.",
                nameof(frame)
            );
        }

        _ring.Write(MemoryMarshal.Cast<byte, float>(frame.Samples));
    }

    /// <summary>Stops playing and releases the device.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        NSNotificationCenter.DefaultCenter.RemoveObserver(_changes);
        _engine.Stop();
        _engine.Dispose();
        _node.Dispose();
    }

    private void Start()
    {
        if (!_engine.StartAndReturnError(out NSError? error))
        {
            throw new InvalidOperationException(
                $"The output device did not start: {error?.LocalizedDescription}"
            );
        }

        LogStarted(OutputLatency);
    }

    // The engine stops when the output device or its format changes; it starts again on the new one.
    private void Restart()
    {
        lock (_gate)
        {
            if (_disposed || _engine.Running)
            {
                return;
            }

            try
            {
                Start();
            }
            catch (InvalidOperationException exception)
            {
                LogRestartFailed(exception);
            }
        }
    }

    // The render thread's pull: queued audio first, then silence, split into the left and right
    // buffers. It must not block or allocate, so it reads the AudioBufferList itself: a buffer count,
    // then pointer-aligned buffers.
    private unsafe int Render(nint isSilence, nint timestamp, uint frameCount, nint bufferList)
    {
        uint count = *(uint*)bufferList;
        var buffers = (ChannelBuffer*)(bufferList + sizeof(nint));
        float* left = (float*)buffers[0].Data;
        float* right = count > 1 ? (float*)buffers[1].Data : left;
        bool heard = false;
        for (int done = 0; done < frameCount; )
        {
            int frames = Math.Min((int)frameCount - done, ScratchSamples / 2);
            Span<float> interleaved = _scratch.AsSpan(0, frames * 2);
            int read = _ring.Read(interleaved);
            interleaved[read..].Clear();
            heard |= read > 0;
            for (int i = 0; i < frames; i++)
            {
                left[done + i] = interleaved[2 * i];
                right[done + i] = interleaved[(2 * i) + 1];
            }

            done += frames;
        }

        *(byte*)isSilence = heard ? (byte)0 : (byte)1;
        return 0;
    }

    // A Core Audio AudioBuffer: one channel's samples here.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ChannelBuffer
    {
        public uint NumberChannels;
        public uint DataByteSize;
        public void* Data;
    }

    [LoggerMessage(2410, LogLevel.Information, "Playing audio, {Latency} to the speaker.")]
    private partial void LogStarted(TimeSpan latency);

    [LoggerMessage(
        2411,
        LogLevel.Error,
        "The output device changed and playing did not start again on the new one."
    )]
    private partial void LogRestartFailed(Exception exception);
}
