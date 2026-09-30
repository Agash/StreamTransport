using System.Numerics;

namespace Agash.StreamTransport.Media;

/// <summary>
/// The affine rows that convert between normalised RGB and Y'CbCr for a colour's matrix and range, as
/// GPU shaders consume them: each output is <c>dot(input, Row.xyz) + Row.w</c>. RGB and Y'CbCr samples
/// are in [0, 1]; on the Y'CbCr side, chroma is centred on <see cref="ChromaCentre"/>, code 128 of 8-bit
/// video.
/// </summary>
public static class ColorConversion
{
    /// <summary>Neutral chroma: code 128 of 255.</summary>
    public const float ChromaCentre = 128f / 255;

    /// <summary>RGB to Y'CbCr: the rows that make Y', Cb and Cr from R, G and B.</summary>
    /// <param name="color">The Y'CbCr colour; an unspecified matrix is BT.709, an unspecified range video range.</param>
    /// <returns>The rows for Y', Cb and Cr.</returns>
    public static (Vector4 Y, Vector4 Cb, Vector4 Cr) RgbToYCbCr(VideoColor color)
    {
        (float kr, float kb) = Coefficients(color.Matrix);
        float kg = 1 - kr - kb;
        (float lumaScale, float lumaOffset, float chromaScale) = Range(color.Range);
        float cbScale = chromaScale / (2 * (1 - kb));
        float crScale = chromaScale / (2 * (1 - kr));
        return (
            new Vector4(kr * lumaScale, kg * lumaScale, kb * lumaScale, lumaOffset),
            new Vector4(-kr * cbScale, -kg * cbScale, (1 - kb) * cbScale, ChromaCentre),
            new Vector4((1 - kr) * crScale, -kg * crScale, -kb * crScale, ChromaCentre)
        );
    }

    /// <summary>
    /// Y'CbCr to RGB, in two steps: luma is first normalised as <c>(Y' - offset) * scale</c>, and chroma
    /// has <see cref="ChromaCentre"/> subtracted; then the rows make R, G and B from the normalised luma
    /// and chroma.
    /// </summary>
    /// <param name="color">The Y'CbCr colour; an unspecified matrix is BT.709, an unspecified range video range.</param>
    /// <returns>The luma offset and scale, and the rows for R, G and B.</returns>
    public static (float LumaOffset, float LumaScale, Vector4 R, Vector4 G, Vector4 B) YCbCrToRgb(
        VideoColor color
    )
    {
        (float kr, float kb) = Coefficients(color.Matrix);
        float kg = 1 - kr - kb;
        (float lumaScale, float lumaOffset, float chromaScale) = Range(color.Range);

        // Normalised chroma is scaled back to [-0.5, 0.5] inside the rows.
        float cr = 2 * (1 - kr) / chromaScale;
        float cb = 2 * (1 - kb) / chromaScale;
        return (
            lumaOffset,
            1 / lumaScale,
            new Vector4(1, 0, cr, 0),
            new Vector4(1, -kb * cb / kg, -kr * cr / kg, 0),
            new Vector4(1, cb, 0, 0)
        );
    }

    // Kr and Kb of a matrix; the identity matrix has no Y'CbCr form and is refused.
    private static (float Kr, float Kb) Coefficients(ColorMatrix matrix) =>
        matrix switch
        {
            ColorMatrix.Bt601 => (0.299f, 0.114f),
            ColorMatrix.Bt2020 => (0.2627f, 0.0593f),
            ColorMatrix.Identity => throw new ArgumentOutOfRangeException(
                nameof(matrix),
                matrix,
                "The identity matrix describes RGB, which has no Y'CbCr form."
            ),
            _ => (0.2126f, 0.0722f),
        };

    // Video range puts luma in 16-235 and chroma in 16-240 (of 255); full range uses 0-255.
    private static (float LumaScale, float LumaOffset, float ChromaScale) Range(ColorRange range) =>
        range == ColorRange.Full ? (1, 0, 1) : (219f / 255, 16f / 255, 224f / 255);
}
