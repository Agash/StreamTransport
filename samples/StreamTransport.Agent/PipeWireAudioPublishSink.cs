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
    private readonly Queue<byte[]> _chunks = new();

    private PipeWireAudioOutput? _output;
    private int _headOffset;     // bytes already consumed from the chunk at the head of the queue
    private int _queuedBytes;
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

        byte[] chunk = samples.ToArray();

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

            _chunks.Enqueue(chunk);
            _queuedBytes += chunk.Length;

            // Overflow: drop the oldest whole chunks (and any partial head) until back under the cap. Dropping
            // old audio rather than new keeps the playout close to live when the consumer falls behind.
            while (_queuedBytes - _headOffset > MaxQueuedBytes && _chunks.Count > 1)
            {
                byte[] dropped = _chunks.Dequeue();
                _queuedBytes -= dropped.Length;
                _headOffset = 0;
            }
        }
    }

    // PipeWire pulls PCM: fill as much of its buffer as we have queued, return the byte count written (0 = let
    // it emit silence). Runs on the PipeWire loop thread. Drains across chunk boundaries.
    private int OnFillSamples(PipeWireAudioOutput sender, Span<byte> dst, int sampleRate, int channels, PwAudioSampleFormat format)
    {
        lock (_gate)
        {
            int written = 0;
            while (written < dst.Length && _chunks.Count > 0)
            {
                byte[] head = _chunks.Peek();
                int available = head.Length - _headOffset;
                int take = Math.Min(available, dst.Length - written);
                head.AsSpan(_headOffset, take).CopyTo(dst.Slice(written, take));
                written += take;
                _headOffset += take;
                _queuedBytes -= take;

                if (_headOffset >= head.Length)
                {
                    _chunks.Dequeue();
                    _headOffset = 0;
                }
            }

            // _queuedBytes tracks remaining (un-consumed) bytes: it was decremented by `take` above, but the
            // head chunk's already-consumed prefix is still counted until the chunk is dequeued. Re-normalise.
            return written;
        }
    }

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
            _chunks.Clear();
            _queuedBytes = 0;
            _headOffset = 0;
        }

        if (output is not null)
        {
            await output.DisposeAsync().ConfigureAwait(false);
        }

        await _context.DisposeAsync().ConfigureAwait(false);
    }
}
#endif
