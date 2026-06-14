using Agash.StreamTransport;
using Agash.StreamTransport.Codecs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// Verifies side-by-side alpha packing: the CPU pack/unpack self-inverts (colour + exact alpha), and the
/// alpha actually survives a real hardware HEVC encode/decode round-trip (NVENC/AMF/VideoToolbox/rkmpp).
/// </summary>
[TestClass]
[DoNotParallelize] // Drives a hardware HEVC encoder/decoder (incl. VAAPI); one GPU session at a time.
public sealed class AlphaPackingTests
{
    [TestMethod]
    public void PackUnpack_RoundTrips_ColourAndAlpha()
    {
        const int width = 256;
        const int height = 64;
        byte[] bgra = BuildGradient(width, height);

        byte[] packed = new byte[AlphaPacking.PackedNv12Length(width, height)];
        AlphaPacking.PackBgraToNv12(bgra, width * 4, width, height, packed);
        Assert.AreEqual(width * 2 * height * 3 / 2, packed.Length, "Packed frame should be 2W x H NV12.");

        byte[] outBgra = new byte[width * height * 4];
        AlphaPacking.UnpackNv12ToBgra(packed, width * 2, height, outBgra);

        // Alpha rides the full-res luma plane in 16..235 limited range (so it matches the GPU pack and
        // interchanges across platforms); it round-trips within the small limited-range quantisation.
        int maxAlphaErr = 0;
        int maxColourErr = 0;
        for (int i = 0; i < width * height; i++)
        {
            maxAlphaErr = Math.Max(maxAlphaErr, Math.Abs(bgra[(i * 4) + 3] - outBgra[(i * 4) + 3]));
            for (int c = 0; c < 3; c++)
            {
                maxColourErr = Math.Max(maxColourErr, Math.Abs(bgra[(i * 4) + c] - outBgra[(i * 4) + c]));
            }
        }

        Assert.IsTrue(maxAlphaErr <= 3, $"Alpha round-trip error {maxAlphaErr} exceeds tolerance.");
        Assert.IsTrue(maxColourErr <= 6, $"Colour round-trip error {maxColourErr} exceeds tolerance.");
    }

    /// <summary>Auto-selected hardware encoder (NVENC/AMF/QSV/VideoToolbox/rkmpp).</summary>
    [TestMethod]
    public void Alpha_AutoEncoder_SurvivesHardwareRoundTrip() => RunAlphaRoundTrip(null);

    /// <summary>NVIDIA NVENC explicitly (Windows/Linux).</summary>
    [TestMethod]
    public void Alpha_Nvenc_SurvivesHardwareRoundTrip() => RunAlphaRoundTrip("hevc_nvenc");

    /// <summary>AMD AMF explicitly (Windows).</summary>
    [TestMethod]
    public void Alpha_Amf_SurvivesHardwareRoundTrip() => RunAlphaRoundTrip("hevc_amf");

    private static void RunAlphaRoundTrip(string? encoderName)
    {
        string? bin = TestNative.FindFFmpegBin();
        if (bin is null)
        {
            Assert.Inconclusive("No bundled FFmpeg native build found; skipping alpha hardware round-trip.");
            return;
        }

        FFmpegLibrary.EnsureLoaded(bin);

        string selected;
        try
        {
            selected = HevcEncoderSelector.Select(encoderName);
        }
        catch (NotSupportedException ex)
        {
            Assert.Inconclusive($"HEVC encoder unavailable: {ex.Message}");
            return;
        }

        const int width = 640;
        const int height = 360;
        byte[] bgra = BuildHalfTransparent(width, height);
        byte[] packed = new byte[AlphaPacking.PackedNv12Length(width, height)];
        AlphaPacking.PackBgraToNv12(bgra, width * 4, width, height, packed);

        IVideoEncoderBackend encoder;
        try
        {
            // Encoder runs at the packed 2W x H dimensions.
            encoder = TestEncoders.Open(selected, width * 2, height, fps: 30, bitrate: 8_000_000);
        }
        catch (HardwareEncoderUnavailableException ex)
        {
            Assert.Inconclusive($"{selected} hardware is not available: {ex.Message}");
            return;
        }

        using (encoder)
        {
            // Decode with the backend that pairs with the encoder, mirroring production (the receive factory
            // uses VAAPI decode on Mesa). Decoding VAAPI output with the hardware-first HevcDecoder instead would
            // exercise its NVDEC/QSV-then-software fallback, which a non-NVIDIA box doesn't use for this stream.
            using IVideoDecoderBackend decoder = selected == "hevc_vaapi"
                ? new VaapiVideoDecoder()
                : new HevcDecoder();
            byte[]? outBgra = null;
            for (int frame = 0; frame < 30 && outBgra is null; frame++)
            {
                byte[]? accessUnit = TestEncoders.EncodeNv12(encoder, packed, width * 2, height);
                if (accessUnit is null)
                {
                    continue;
                }

                if (decoder.TryDecode(accessUnit, 0, 0, out VideoFrame decoded, out _))
                {
                    Assert.AreEqual(width * 2, decoded.Width, "Decoded width should be the packed 2W.");
                    byte[] tmp = new byte[width * height * 4];
                    AlphaPacking.UnpackNv12ToBgra(decoded.Pixels.ToArray(), decoded.Width, decoded.Height, tmp);
                    outBgra = tmp;
                }
            }

            Assert.IsNotNull(outBgra, "Expected at least one decoded frame from the alpha round-trip.");

            // The opaque region must stay opaque and the transparent region transparent through lossy HEVC.
            byte aOpaque = outBgra[(((height / 2) * width) + (width / 4)) * 4 + 3];
            byte aTransparent = outBgra[(((height / 2) * width) + (3 * width / 4)) * 4 + 3];
            Assert.IsTrue(aOpaque > 200, $"Opaque alpha {aOpaque} should stay high.");
            Assert.IsTrue(aTransparent < 55, $"Transparent alpha {aTransparent} should stay low.");
        }
    }

    private static byte[] BuildGradient(int width, int height)
    {
        byte[] bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int p = ((y * width) + x) * 4;
                bgra[p] = 128;                  // B
                bgra[p + 1] = (byte)(y * 4);    // G
                bgra[p + 2] = (byte)x;          // R
                bgra[p + 3] = (byte)x;          // A: horizontal 0..255 gradient
            }
        }

        return bgra;
    }

    private static byte[] BuildHalfTransparent(int width, int height)
    {
        byte[] bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int p = ((y * width) + x) * 4;
                bool opaque = x < width / 2;
                bgra[p] = (byte)(opaque ? 255 : 50);
                bgra[p + 1] = (byte)(opaque ? 255 : 100);
                bgra[p + 2] = (byte)(opaque ? 255 : 200);
                bgra[p + 3] = (byte)(opaque ? 255 : 0);
            }
        }

        return bgra;
    }
}
