using System.Numerics;
using System.Runtime.InteropServices;

namespace Agash.StreamTransport.WebRtc.Rtp;

/// <summary>XOR parity over packet bytes, a vector at a time where the hardware allows.</summary>
internal static class Parity
{
    /// <summary>XORs <paramref name="source"/> into <paramref name="parity"/>, which is at least as long.</summary>
    /// <param name="parity">The running parity.</param>
    /// <param name="source">The bytes folded in.</param>
    public static void Xor(Span<byte> parity, ReadOnlySpan<byte> source)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            Span<Vector<byte>> wide = MemoryMarshal.Cast<byte, Vector<byte>>(
                parity[..source.Length]
            );
            ReadOnlySpan<Vector<byte>> from = MemoryMarshal.Cast<byte, Vector<byte>>(source);
            for (int v = 0; v < from.Length; v++)
            {
                wide[v] ^= from[v];
            }

            i = from.Length * Vector<byte>.Count;
        }

        for (; i < source.Length; i++)
        {
            parity[i] ^= source[i];
        }
    }
}
