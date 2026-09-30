using System.Runtime.InteropServices;
using Agash.StreamTransport.Audio;
using Agash.StreamTransport.Media;
using PipeWire.NET;
using PipeWire.NET.Media;
using AudioFormat = Agash.StreamTransport.Media.AudioFormat;
using AudioFrame = Agash.StreamTransport.Media.AudioFrame;

namespace Agash.StreamTransport.Linux.PipeWire;

/// <summary>
/// Plays audio into PipeWire: the default output, or a node it is linked to. It takes 48 kHz stereo
/// float. Audio waits in a short buffer for the graph; when the graph runs dry it plays silence, and
/// when audio arrives faster than it plays the oldest is dropped, so output stays near real time.
/// </summary>
public sealed class PipeWireAudioSink : IAudioSink, IDisposable
{
    // Two hundred milliseconds of stereo: jitter the playout buffer did not absorb.
    private const int BufferedSamples = 48_000 / 5 * 2;

    private readonly FloatRing _ring = new(BufferedSamples);
    private readonly PipeWireAudioOutput _output;

    /// <summary>A sink that plays from when it is made.</summary>
    /// <param name="context">A started connection to the daemon.</param>
    /// <param name="options">Where to play; defaults when null.</param>
    public PipeWireAudioSink(PipeWireContext context, PipeWireAudioOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        options ??= new PipeWireAudioOptions();
        AudioFormat format = PipeWireAudioSource.Transport;
        _output = new PipeWireAudioOutput(
            context,
            options.NodeName,
            format.SampleRate,
            format.Channels,
            AudioSampleFormat.F32Le
        );
        _output.FillSamples += Fill;
        try
        {
            _output.Connect(
                options.TargetNodeId ?? PipeWireAudioOutput.AnyNode,
                options.TargetObject,
                autoConnect: options.AutoConnect
            );
        }
        catch
        {
            _output.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public AudioConstraints Constraints { get; } = new([48_000], [SampleFormat.F32], 2);

    /// <summary>The node's id once the daemon has assigned one.</summary>
    public uint? NodeId => _output.NodeId;

    /// <summary>
    /// How long audio takes from this sink to the device: what waits in its buffer, plus PipeWire's
    /// playback latency. What a session's audio output offset is measured against. Null until the
    /// stream is running.
    /// </summary>
    public TimeSpan? OutputLatency =>
        _output.PlaybackLatency is { } playback
            ? playback
                + TimeSpan.FromTicks(
                    (long)_ring.Count
                        * TimeSpan.TicksPerSecond
                        / (
                            PipeWireAudioSource.Transport.SampleRate
                            * PipeWireAudioSource.Transport.Channels
                        )
                )
            : null;

    /// <summary>Completes when the node has an id.</summary>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>The node id.</returns>
    public Task<uint> WaitForNodeIdAsync(CancellationToken cancellationToken = default) =>
        _output.WaitForNodeIdAsync(cancellationToken);

    /// <inheritdoc/>
    public void OnFrame(in AudioFrame frame)
    {
        if (frame.Format != PipeWireAudioSource.Transport)
        {
            throw new ArgumentException(
                $"The sink takes {PipeWireAudioSource.Transport}; the audio is {frame.Format}.",
                nameof(frame)
            );
        }

        _ring.Write(MemoryMarshal.Cast<byte, float>(frame.Samples));
    }

    /// <summary>Stops playing and removes the node.</summary>
    public void Dispose() => _output.Dispose();

    // Runs on the PipeWire loop thread: queued audio, then silence for the rest of the buffer.
    private int Fill(
        PipeWireAudioOutput sender,
        Span<byte> samples,
        int sampleRate,
        int channels,
        AudioSampleFormat format
    )
    {
        Span<float> floats = MemoryMarshal.Cast<byte, float>(samples);
        int read = _ring.Read(floats);
        floats[read..].Clear();
        return samples.Length;
    }
}
