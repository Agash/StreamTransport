namespace Agash.StreamTransport.Codecs;

/// <summary>
/// Side-by-side alpha packing: carry a BGRA frame's transparency through an ordinary opaque HEVC/AV1
/// encoder by laying colour and alpha next to each other in one <c>2W x H</c> frame - left half = colour,
/// right half = the alpha stored directly as luma. Because alpha lands in the (full-resolution, not
/// chroma-subsampled) luma plane, it survives 4:2:0 encode crisply. The GPU shaders (D3D11/Metal) produce
/// the identical layout; this CPU reference serves already-CPU sources (PipeWire MemPtr, synthetic) and is
/// the byte-for-byte oracle the round-trip tests check.
///
/// Both directions use the same BT.709 limited-range colour matrix as the decode-side NV12->BGRA shader,
/// so colour round-trips; alpha is stored and read back as a raw 8-bit luma sample.
/// </summary>
internal static class AlphaPacking
{
    /// <summary>The NV12 byte length of the packed frame for a <paramref name="width"/> x <paramref name="height"/> source.</summary>
    public static int PackedNv12Length(int width, int height) => (width * 2) * height * 3 / 2;

    /// <summary>
    /// Pack a BGRA frame into a <c>2W x H</c> NV12 buffer: left half colour, right half alpha-as-luma with
    /// neutral chroma. <paramref name="packedNv12"/> must be <see cref="PackedNv12Length"/> bytes.
    /// </summary>
    public static void PackBgraToNv12(ReadOnlySpan<byte> bgra, int stride, int width, int height, Span<byte> packedNv12)
    {
        int packedW = width * 2;
        int lumaSize = packedW * height;

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            int lumaRow = y * packedW;
            int uvRow = lumaSize + ((y / 2) * packedW);
            for (int x = 0; x < width; x++)
            {
                int p = row + (x * 4);
                byte b = bgra[p];
                byte g = bgra[p + 1];
                byte r = bgra[p + 2];
                byte a = bgra[p + 3];

                // Colour luma (left) and alpha-as-luma (right), both BT.709 limited range. Alpha uses the
                // same 16..235 mapping the GPU pack gets for free from the encoder's in-ASIC RGB->YUV of a
                // grey (a,a,a) pixel (Y = 16 + 219/255 * a, matrix-independent), so a CPU-packed frame and a
                // GPU-packed frame are byte-compatible and interchange across platforms.
                packedNv12[lumaRow + x] = (byte)((((47 * r) + (157 * g) + (16 * b)) >> 8) + 16);
                packedNv12[lumaRow + width + x] = (byte)(16 + (((a * 219) + 127) / 255));

                if ((y & 1) == 0 && (x & 1) == 0)
                {
                    byte cb = (byte)((((-26 * r) - (87 * g) + (112 * b)) >> 8) + 128);
                    byte cr = (byte)((((112 * r) - (102 * g) - (10 * b)) >> 8) + 128);
                    packedNv12[uvRow + x] = cb;          // colour chroma (left)
                    packedNv12[uvRow + x + 1] = cr;
                    packedNv12[uvRow + width + x] = 128;  // neutral chroma over the alpha half (right)
                    packedNv12[uvRow + width + x + 1] = 128;
                }
            }
        }
    }

    /// <summary>
    /// Unpack a <c>2W x H</c> NV12 buffer produced by <see cref="PackBgraToNv12"/> (and round-tripped
    /// through the codec) back into a tightly-packed <c>W x H</c> BGRA buffer (4 bytes/pixel, alpha set).
    /// </summary>
    public static void UnpackNv12ToBgra(ReadOnlySpan<byte> packedNv12, int packedWidth, int height, Span<byte> bgra)
    {
        int width = packedWidth / 2;
        int lumaSize = packedWidth * height;

        for (int y = 0; y < height; y++)
        {
            int lumaRow = y * packedWidth;
            int uvRow = lumaSize + ((y / 2) * packedWidth);
            int dstRow = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int yy = packedNv12[lumaRow + x] - 16;
                int cb = packedNv12[uvRow + (x & ~1)] - 128;
                int cr = packedNv12[uvRow + (x & ~1) + 1] - 128;

                // BT.709 limited -> full-range RGB (matches D3D11Nv12ToBgraConverter).
                int r = ((298 * yy) + (459 * cr)) >> 8;
                int g = ((298 * yy) - (55 * cb) - (136 * cr)) >> 8;
                int b = ((298 * yy) + (541 * cb)) >> 8;

                int d = dstRow + (x * 4);
                bgra[d] = Clamp(b);
                bgra[d + 1] = Clamp(g);
                bgra[d + 2] = Clamp(r);
                // Alpha = right-half luma, expanded back from the 16..235 limited range used on pack.
                bgra[d + 3] = Clamp((((packedNv12[lumaRow + width + x] - 16) * 255) + 109) / 219);
            }
        }
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
