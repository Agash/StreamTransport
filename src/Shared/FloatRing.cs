namespace Agash.StreamTransport.Audio;

// Interleaved samples between a producer and a device thread. When full, the oldest samples go, so the
// output stays near real time instead of falling behind.
internal sealed class FloatRing(int capacity)
{
    private readonly float[] _samples = new float[capacity];
    private readonly Lock _gate = new();
    private int _start;
    private int _count;

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            if (samples.Length > _samples.Length)
            {
                samples = samples[^_samples.Length..];
            }

            int overflow = _count + samples.Length - _samples.Length;
            if (overflow > 0)
            {
                _start = (_start + overflow) % _samples.Length;
                _count -= overflow;
            }

            int end = (_start + _count) % _samples.Length;
            int first = Math.Min(samples.Length, _samples.Length - end);
            samples[..first].CopyTo(_samples.AsSpan(end));
            samples[first..].CopyTo(_samples);
            _count += samples.Length;
        }
    }

    // Reads up to the destination's length and returns how many samples were read.
    public int Read(Span<float> destination)
    {
        lock (_gate)
        {
            int taken = Math.Min(destination.Length, _count);
            int first = Math.Min(taken, _samples.Length - _start);
            _samples.AsSpan(_start, first).CopyTo(destination);
            _samples.AsSpan(0, taken - first).CopyTo(destination[first..]);
            _start = (_start + taken) % _samples.Length;
            _count -= taken;
            return taken;
        }
    }
}
