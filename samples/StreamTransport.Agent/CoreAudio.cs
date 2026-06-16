#if MACOS_HEAD
using System.Runtime.Versioning;
using AudioToolbox;
using AudioUnit;
using AudioFrame = Agash.StreamTransport.AudioFrame;
using AudioSampleFormat = Agash.StreamTransport.AudioSampleFormat;

namespace StreamTransport.Agent;

/// <summary>
/// macOS audio playout (the macOS companion to <see cref="WasapiAudioPublishSink"/> / PipeWireAudioPublishSink):
/// renders decoded PCM to the default output device via the CoreAudio default-output AudioUnit, so the system
/// (and OBS desktop-audio capture) hears it. The AudioUnit's realtime thread <i>pulls</i> through a render
/// callback that drains the shared <see cref="PullAudioRingBuffer"/> (bounded backlog; underrun fills silence to
/// keep the clock) - the same pull-model the WASAPI and PipeWire sinks use. macOS-only.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class CoreAudioPublishSink : IAudioFrameSink, IDisposable
{
    private const int MaxQueuedBytes = 192_000 / 5; // ~200 ms at 48 kHz stereo S16.

    private readonly PullAudioRingBuffer _ring = new(MaxQueuedBytes);
    private readonly Lock _gate = new();
    private AudioUnit.AudioUnit? _unit;
    private int _channels;
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

            if (_unit is null)
            {
                Initialize(frame.SampleRate, Math.Max(1, frame.Channels));
            }
        }

        _ring.Write(samples);
    }

    // Lazy init on the first frame, when rate/channels are known: default output unit, interleaved S16 so the
    // ring's bytes copy straight into the render buffer (no format conversion).
    private void Initialize(int sampleRate, int channels)
    {
        _channels = channels;
        var desc = new AudioComponentDescription
        {
            ComponentType = AudioComponentType.Output,
            ComponentSubType = AudioUnitSubType.DefaultOutput,
            ComponentManufacturer = AudioComponentManufacturerType.Apple,
        };
        AudioComponent comp = AudioComponent.FindComponent(ref desc)
            ?? throw new InvalidOperationException("No default output AudioComponent found.");
        AudioUnit.AudioUnit unit = comp.CreateAudioUnit();

        var asbd = new AudioStreamBasicDescription
        {
            Format = AudioFormatType.LinearPCM,
            FormatFlags = AudioFormatFlags.IsSignedInteger | AudioFormatFlags.IsPacked,
            SampleRate = sampleRate,
            ChannelsPerFrame = channels,
            BitsPerChannel = 16,
            BytesPerFrame = 2 * channels,
            FramesPerPacket = 1,
            BytesPerPacket = 2 * channels,
        };
        unit.SetFormat(asbd, AudioUnitScopeType.Input, 0);
        unit.SetRenderCallback(Render, AudioUnitScopeType.Input, 0);
        unit.Initialize();
        unit.Start();
        _unit = unit;
    }

    // Pull adapter: CoreAudio asks for `numberFrames`; drain the ring into the output buffer and zero-fill any
    // shortfall so playback stays continuous. Runs on the realtime audio thread.
    private unsafe AudioUnitStatus Render(AudioUnitRenderActionFlags actionFlags, AudioTimeStamp timeStamp,
        uint busNumber, uint numberFrames, AudioBuffers data)
    {
        int count = (int)numberFrames * 2 * _channels;
        var dst = new Span<byte>((void*)data[0].Data, count);
        int written = _ring.Read(dst);
        if (written < count)
        {
            dst[written..].Clear();
        }

        return AudioUnitStatus.NoError;
    }

    public void Dispose()
    {
        AudioUnit.AudioUnit? unit;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            unit = _unit;
            _unit = null;
            _ring.Clear();
        }

        if (unit is not null)
        {
            try { unit.Stop(); } catch { /* already stopped */ }
            unit.Dispose();
        }
    }
}
#endif
