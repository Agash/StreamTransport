using System.Buffers.Binary;
using System.Text;

namespace Agash.StreamTransport.WebRtc.Rtp;

/// <summary>
/// An identification-tag as RTP and RTCP carry it to name a stream's media section (RFC 9143 section 15):
/// 1 to 16 bytes of UTF-8, the most the one-byte header extension form holds. Held by value, so reading
/// one from a packet allocates nothing.
/// </summary>
public readonly record struct RtpMid
{
    /// <summary>The longest identification-tag, in bytes of UTF-8.</summary>
    public const int MaximumLength = 16;

    private readonly UInt128 _bytes;
    private readonly byte _length;

    private RtpMid(UInt128 bytes, byte length)
    {
        _bytes = bytes;
        _length = length;
    }

    /// <summary>The tag's length in bytes; 0 for the default value, which names nothing.</summary>
    public int Length => _length;

    /// <summary>Reads a tag from its UTF-8 bytes.</summary>
    /// <param name="utf8">The bytes, 1 to <see cref="MaximumLength"/>.</param>
    /// <param name="mid">The tag.</param>
    /// <returns>Whether the bytes are a tag of a length this type holds.</returns>
    public static bool TryCreate(ReadOnlySpan<byte> utf8, out RtpMid mid)
    {
        mid = default;
        if (utf8.Length is 0 or > MaximumLength)
        {
            return false;
        }

        Span<byte> padded = stackalloc byte[MaximumLength];
        padded.Clear();
        utf8.CopyTo(padded);
        mid = new RtpMid(BinaryPrimitives.ReadUInt128BigEndian(padded), (byte)utf8.Length);
        return true;
    }

    /// <summary>The tag of an SDP <c>a=mid</c> value.</summary>
    /// <param name="mid">The value.</param>
    /// <param name="tag">The tag.</param>
    /// <returns>Whether the value fits <see cref="MaximumLength"/> bytes of UTF-8.</returns>
    public static bool TryCreate(string mid, out RtpMid tag)
    {
        ArgumentNullException.ThrowIfNull(mid);
        tag = default;
        Span<byte> utf8 = stackalloc byte[MaximumLength];
        return Encoding.UTF8.TryGetBytes(mid, utf8, out int written)
            && TryCreate(utf8[..written], out tag);
    }

    /// <summary>Writes the tag's UTF-8 bytes.</summary>
    /// <param name="destination">Where they go, at least <see cref="Length"/> bytes.</param>
    /// <returns>The bytes written.</returns>
    public int CopyTo(Span<byte> destination)
    {
        Span<byte> padded = stackalloc byte[MaximumLength];
        BinaryPrimitives.WriteUInt128BigEndian(padded, _bytes);
        padded[.._length].CopyTo(destination);
        return _length;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        Span<byte> padded = stackalloc byte[MaximumLength];
        BinaryPrimitives.WriteUInt128BigEndian(padded, _bytes);
        return Encoding.UTF8.GetString(padded[.._length]);
    }
}
