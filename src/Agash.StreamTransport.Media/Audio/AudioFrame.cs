using System.Buffers;
using System.Collections.Concurrent;

namespace Agash.StreamTransport.Media;

/// <summary>The sample type of interleaved PCM.</summary>
public enum SampleFormat
{
    /// <summary>16-bit signed integers.</summary>
    S16,

    /// <summary>32-bit IEEE floats in [-1, 1].</summary>
    F32,
}

/// <summary>The format of interleaved PCM audio.</summary>
/// <param name="SampleFormat">The sample type.</param>
/// <param name="SampleRate">Samples per second per channel.</param>
/// <param name="Channels">Channels, interleaved.</param>
public readonly record struct AudioFormat(SampleFormat SampleFormat, int SampleRate, int Channels)
{
    /// <summary>Bytes in one sample of one channel.</summary>
    public int BytesPerSample => SampleFormat == SampleFormat.S16 ? 2 : 4;

    /// <summary>Bytes in one sample of every channel.</summary>
    public int BytesPerFrame => BytesPerSample * Channels;
}

/// <summary>
/// Interleaved PCM audio, borrowed: valid only for the call it is passed to. <see cref="Retain"/>
/// copies it into pooled memory for a consumer that keeps it.
/// </summary>
/// <param name="samples">The interleaved samples.</param>
/// <param name="format">Their format.</param>
/// <param name="timestamp">When the first sample was captured.</param>
public readonly ref struct AudioFrame(
    ReadOnlySpan<byte> samples,
    AudioFormat format,
    MediaTimestamp timestamp
)
{
    /// <summary>The interleaved samples.</summary>
    public ReadOnlySpan<byte> Samples { get; } = samples;

    /// <summary>Their format.</summary>
    public AudioFormat Format { get; } = format;

    /// <summary>When the first sample was captured.</summary>
    public MediaTimestamp Timestamp { get; } = timestamp;

    /// <summary>Samples per channel.</summary>
    public int SampleCount => Samples.Length / Format.BytesPerFrame;

    /// <summary>How long the frame plays.</summary>
    public TimeSpan Duration => new ClockRate(Format.SampleRate).ToTimeSpan(SampleCount);

    /// <summary>Keeps the frame past the call, in pooled memory.</summary>
    /// <returns>A lease, released exactly once by disposing it.</returns>
    public AudioFrameLease Retain() => AudioFrameLease.Of(in this);
}

/// <summary>
/// An owned audio frame in pooled memory; disposing it returns the lease and its memory to a pool, so
/// keeping a frame allocates nothing. A lease is not used after its <see cref="Dispose"/>, as a pooled
/// one holds another frame by then.
/// </summary>
public sealed class AudioFrameLease : IDisposable
{
    private static readonly ConcurrentBag<AudioFrameLease> Spare = [];

    private byte[] _buffer;
    private int _length;
    private int _disposed;

    private AudioFrameLease(int length) => _buffer = ArrayPool<byte>.Shared.Rent(length);

    /// <summary>The samples' format.</summary>
    public AudioFormat Format { get; private set; }

    /// <summary>When the first sample was captured.</summary>
    public MediaTimestamp Timestamp { get; private set; }

    /// <summary>The frame, valid while the lease is.</summary>
    /// <exception cref="ObjectDisposedException">The lease was released.</exception>
    public AudioFrame Frame
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return new AudioFrame(_buffer.AsSpan(0, _length), Format, Timestamp);
        }
    }

    internal static AudioFrameLease Of(in AudioFrame frame)
    {
        int length = frame.Samples.Length;
        if (Spare.TryTake(out AudioFrameLease? lease))
        {
            if (lease._buffer.Length < length)
            {
                ArrayPool<byte>.Shared.Return(lease._buffer);
                lease._buffer = ArrayPool<byte>.Shared.Rent(length);
            }

            Volatile.Write(ref lease._disposed, 0);
        }
        else
        {
            lease = new AudioFrameLease(length);
        }

        lease._length = length;
        frame.Samples.CopyTo(lease._buffer);
        lease.Format = frame.Format;
        lease.Timestamp = frame.Timestamp;
        return lease;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Spare.Add(this);
        }
    }
}

/// <summary>Receives audio from a source on the source's thread.</summary>
public interface IAudioFrameConsumer
{
    /// <summary>Audio; valid only during the call.</summary>
    /// <param name="frame">The frame.</param>
    void OnFrame(in AudioFrame frame);
}

/// <summary>A source of audio that pushes frames to its consumer as they are made.</summary>
public interface IAudioSource
{
    /// <summary>The format the source produces.</summary>
    AudioFormat Format { get; }

    /// <summary>Starts delivering frames to a consumer.</summary>
    /// <param name="consumer">Where frames go.</param>
    /// <returns>A handle that stops delivery when disposed.</returns>
    IDisposable Connect(IAudioFrameConsumer consumer);
}
