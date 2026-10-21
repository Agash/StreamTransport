using System.Numerics.Tensors;

namespace Agash.StreamTransport.WebRtc.Rtp;

/// <summary>XOR parity over packet bytes, vectorized by <see cref="TensorPrimitives"/>.</summary>
internal static class Parity
{
    /// <summary>XORs <paramref name="source"/> into <paramref name="parity"/>, which is at least as long.</summary>
    /// <param name="parity">The running parity.</param>
    /// <param name="source">The bytes folded in.</param>
    public static void Xor(Span<byte> parity, ReadOnlySpan<byte> source)
    {
        Span<byte> into = parity[..source.Length];
        TensorPrimitives.Xor(into, source, into);
    }
}
