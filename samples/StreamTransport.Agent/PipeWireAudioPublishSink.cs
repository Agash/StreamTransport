#if HAS_PIPEWIRE
using System.Runtime.Versioning;
using Agash.StreamTransport;
using PipeWire.NET;
using PwAudioSampleFormat = PipeWire.NET.AudioSampleFormat;
using AudioFrame = Agash.StreamTransport.AudioFrame;

namespace StreamTransport.Agent;

/// <summary>
/// Publishes decoded PCM audio to a PipeWire playback node (Linux) so downstream apps (OBS) can pick it up -
/// the audio companion to <see cref="PipeWireVideoPublishSink"/>. On Linux PipeWire is the audio sink for the
/// whole pipeline; the Windows/macOS audio outputs (NAudio / CoreAudio) are separate sinks for those platforms.
///
/// <para>Like the video output, PipeWire <i>pulls</i> samples by invoking
/// <see cref="PipeWireAudioOutput.FillSamples"/> on its loop thread, while the decoder <i>pushes</i> via
/// <see cref="Submit"/>. So decoded PCM is queued here and drained on each pull; an underrun emits silence
/// (the consumer keeps its clock), and the queue is bounded so a slow consumer drops the oldest audio rather
/// than growing latency without bound.</para>
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class PipeWireAudioPublishSink : IAudioFrameSink, IAsyncDisposable
{
    // Bound the queued backlog so latency stays bounded: at 48 kHz stereo S16 (~192 kB/s) this is ~200 ms.
    private const int MaxQueuedBytes = 192_000 / 5;

    private readonly PipeWireContext _context;
    private readonly string _nodeName;
    private readonly Lock _gate = new();
    private readonly PullAudioRingBuffer _ring = new(MaxQueuedBytes);

    private PipeWireAudioOutput? _output;
    private bool _disposed;

    private PipeWireAudioPublishSink(PipeWireContext context, string nodeName)
    {
        _context = context;
        _nodeName = nodeName;
    }

    /// <summary>Start the PipeWire loop and return a sink ready to publish under <paramref name="nodeName"/>.</summary>
    public static async Task<PipeWireAudioPublishSink> CreateAsync(string nodeName)
    {
        var context = new PipeWireContext();
        await context.StartAsync().ConfigureAwait(false);
        return new PipeWireAudioPublishSink(context, nodeName);
    }

    public void Submit(AudioFrame frame)
    {
        ReadOnlySpan<byte> samples = frame.Samples.Span;
        if (samples.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Create the output lazily on the first frame, when the stream's rate/channels/format are known.
            if (_output is null)
            {
                var output = new PipeWireAudioOutput(
                    _context, _nodeName, frame.SampleRate, frame.Channels, MapFormat(frame.Format));
                output.FillSamples += OnFillSamples;
                output.Connect();
                _output = output;
            }
        }

        _ring.Write(samples);
    }

    // PipeWire pulls PCM: fill as much of its buffer as the ring holds, return the byte count written (0 = let
    // it emit silence). Runs on the PipeWire loop thread.
    private int OnFillSamples(PipeWireAudioOutput sender, Span<byte> dst, int sampleRate, int channels, PwAudioSampleFormat format)
        => _ring.Read(dst);

    private static PwAudioSampleFormat MapFormat(Agash.StreamTransport.AudioSampleFormat format) => format switch
    {
        Agash.StreamTransport.AudioSampleFormat.S16 => PwAudioSampleFormat.S16Le,
        Agash.StreamTransport.AudioSampleFormat.F32 => PwAudioSampleFormat.F32Le,
        _ => PwAudioSampleFormat.S16Le,
    };

    public async ValueTask DisposeAsync()
    {
        PipeWireAudioOutput? output;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            output = _output;
            _output = null;
            _ring.Clear();
        }

        if (output is not null)
        {
            await output.DisposeAsync().ConfigureAwait(false);
        }

        await _context.DisposeAsync().ConfigureAwait(false);
    }
}
#endif
