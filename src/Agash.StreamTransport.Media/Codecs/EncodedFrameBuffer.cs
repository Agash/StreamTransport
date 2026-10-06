using System.Buffers;

namespace Agash.StreamTransport.Media;

/// <summary>
/// An encoded frame in a buffer rented from <see cref="ArrayPool{T}.Shared"/>. Its holder owns the buffer
/// and returns it by disposing; a default value owns nothing.
/// </summary>
public readonly struct EncodedFrameBuffer : IDisposable, IEquatable<EncodedFrameBuffer>
{
    private readonly byte[]? _array;

    /// <summary>Takes ownership of a rented buffer holding a frame in its first bytes.</summary>
    /// <param name="array">A buffer rented from <see cref="ArrayPool{T}.Shared"/>.</param>
    /// <param name="length">The frame's length in bytes.</param>
    public EncodedFrameBuffer(byte[] array, int length)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, array.Length);
        _array = array;
        Length = length;
    }

    /// <summary>The frame's bytes.</summary>
    public ReadOnlySpan<byte> Span => _array.AsSpan(0, Length);

    /// <summary>The frame's bytes, for use across an await.</summary>
    public ReadOnlyMemory<byte> Memory => _array.AsMemory(0, Length);

    /// <summary>The frame's length in bytes.</summary>
    public int Length { get; }

    /// <summary>Returns the buffer to the pool.</summary>
    public void Dispose()
    {
        if (_array is not null)
        {
            ArrayPool<byte>.Shared.Return(_array);
        }
    }

    /// <inheritdoc/>
    public bool Equals(EncodedFrameBuffer other) =>
        ReferenceEquals(_array, other._array) && Length == other.Length;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is EncodedFrameBuffer other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_array, Length);

    /// <summary>Whether two values hold the same buffer and length.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>True when they are equal.</returns>
    public static bool operator ==(EncodedFrameBuffer left, EncodedFrameBuffer right) =>
        left.Equals(right);

    /// <summary>Whether two values hold different buffers or lengths.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>True when they differ.</returns>
    public static bool operator !=(EncodedFrameBuffer left, EncodedFrameBuffer right) =>
        !left.Equals(right);
}
