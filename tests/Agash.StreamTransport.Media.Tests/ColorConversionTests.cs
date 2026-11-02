using System.Numerics;

namespace Agash.StreamTransport.Media.Tests;

[TestClass]
public sealed class ColorConversionTests
{
    public static IEnumerable<object[]> Colors =>
        from matrix in new[] { ColorMatrix.Bt601, ColorMatrix.Bt709, ColorMatrix.Bt2020 }
        from range in new[] { ColorRange.Limited, ColorRange.Full }
        select new object[] { matrix, range };

    [TestMethod]
    public void ConvertedTo_RgbToYCbCr_TakesTheBt709MatrixInVideoRange() =>
        Assert.AreEqual(
            VideoColor.Bt709 with
            {
                Transfer = TransferFunction.Srgb,
            },
            VideoColor.Srgb.ConvertedTo(PixelFormat.Nv12)
        );

    [TestMethod]
    public void ConvertedTo_YCbCrToRgb_IsFullRangeRgbKeepingItsTransfer() =>
        Assert.AreEqual(
            VideoColor.Srgb with
            {
                Transfer = TransferFunction.Bt709,
            },
            VideoColor.Bt709.ConvertedTo(PixelFormat.Bgra)
        );

    [TestMethod]
    public void ConvertedTo_WithinAFamily_KeepsTheColour()
    {
        VideoColor bt601 = VideoColor.Bt709 with { Matrix = ColorMatrix.Bt601 };
        Assert.AreEqual(bt601, bt601.ConvertedTo(PixelFormat.I420));
        Assert.AreEqual(VideoColor.Srgb, VideoColor.Srgb.ConvertedTo(PixelFormat.Rgba));
    }

    [TestMethod]
    [DynamicData(nameof(Colors))]
    public void RoundTrip_ReturnsTheColour(ColorMatrix matrix, ColorRange range)
    {
        VideoColor color = VideoColor.Bt709 with { Matrix = matrix, Range = range };
        foreach (
            Vector3 rgb in new[]
            {
                new Vector3(1, 0, 0),
                new Vector3(0.2f, 0.7f, 0.4f),
                new Vector3(0.5f),
                Vector3.One,
            }
        )
        {
            Vector3 back = ToRgb(ToYcc(rgb, color), color);
            Assert.AreEqual(rgb.X, back.X, 1e-4);
            Assert.AreEqual(rgb.Y, back.Y, 1e-4);
            Assert.AreEqual(rgb.Z, back.Z, 1e-4);
        }
    }

    [TestMethod]
    public void VideoRange_PutsBlackAndWhiteAt16And235()
    {
        Assert.AreEqual(16, ToYcc(Vector3.Zero, VideoColor.Bt709).X * 255, 1e-3);
        Assert.AreEqual(235, ToYcc(Vector3.One, VideoColor.Bt709).X * 255, 1e-3);
    }

    [TestMethod]
    public void Grey_HasNeutralChroma()
    {
        Vector3 ycc = ToYcc(new Vector3(0.3f), VideoColor.Bt709);
        Assert.AreEqual(128, ycc.Y * 255, 1e-3);
        Assert.AreEqual(128, ycc.Z * 255, 1e-3);
    }

    [TestMethod]
    public void Bt709VideoRange_MatchesTheWellKnownCoefficients()
    {
        (_, float scale, Vector4 r, Vector4 g, Vector4 b) = ColorConversion.YCbCrToRgb(
            VideoColor.Bt709
        );
        Assert.AreEqual(1.164, scale, 1e-3);
        Assert.AreEqual(1.793, r.Z, 1e-3);
        Assert.AreEqual(-0.213, g.Y, 1e-3);
        Assert.AreEqual(-0.533, g.Z, 1e-3);
        Assert.AreEqual(2.112, b.Y, 1e-3);
    }

    [TestMethod]
    public void Identity_HasNoYCbCrForm() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ColorConversion.RgbToYCbCr(VideoColor.Srgb)
        );

    private static Vector3 ToYcc(Vector3 rgb, VideoColor color)
    {
        (Vector4 y, Vector4 cb, Vector4 cr) = ColorConversion.RgbToYCbCr(color);
        return new Vector3(Apply(y, rgb), Apply(cb, rgb), Apply(cr, rgb));
    }

    private static Vector3 ToRgb(Vector3 ycc, VideoColor color)
    {
        (float offset, float scale, Vector4 r, Vector4 g, Vector4 b) = ColorConversion.YCbCrToRgb(
            color
        );
        Vector3 normalised = new(
            (ycc.X - offset) * scale,
            ycc.Y - ColorConversion.ChromaCentre,
            ycc.Z - ColorConversion.ChromaCentre
        );
        return new Vector3(Apply(r, normalised), Apply(g, normalised), Apply(b, normalised));
    }

    private static float Apply(Vector4 row, Vector3 input) =>
        Vector3.Dot(new Vector3(row.X, row.Y, row.Z), input) + row.W;
}
