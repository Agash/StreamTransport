using System.Diagnostics;
using System.Runtime.InteropServices;
using Agash.StreamTransport;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// A test <see cref="IAudioFrameSource"/> that emits a 440 Hz sine wave as 20 ms / 48 kHz mono S16
/// frames, paced to roughly real time so the sender produces a realistic RTP cadence.
/// </summary>
internal sealed class ToneAudioSource : IAudioFrameSource
{
    private const int SampleRate = 48_000;
    private const int SamplesPerFrame = SampleRate / 50; // 20 ms.
    private static readonly long s_frameTicks = Stopwatch.Frequency / 50;

    private long _nextEmitTicks = Stopwatch.GetTimestamp();
    private double _phase;

    public bool TryGetFrame(out AudioFrame frame)
    {
        long now = Stopwatch.GetTimestamp();
        if (now < _nextEmitTicks)
        {
            frame = default;
            return false;
        }

        _nextEmitTicks = now + s_frameTicks;

        short[] pcm = new short[SamplesPerFrame];
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)(Math.Sin(_phase) * 8000);
            _phase += 2 * Math.PI * 440 / SampleRate;
        }

        byte[] bytes = MemoryMarshal.AsBytes(pcm.AsSpan()).ToArray();
        long presentationTimeNs = now * (1_000_000_000L / Stopwatch.Frequency);
        frame = new AudioFrame(bytes, AudioSampleFormat.S16, SampleRate, 1, presentationTimeNs);
        return true;
    }
}

/// <summary>A test <see cref="IAudioFrameSink"/> that counts frames and signals when a target is reached.</summary>
internal sealed class CollectingAudioSink(int target) : IAudioFrameSink
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public Task Reached => _reached.Task;

    public void Submit(AudioFrame frame)
    {
        if (Interlocked.Increment(ref _count) >= target)
        {
            _reached.TrySetResult();
        }
    }
}
