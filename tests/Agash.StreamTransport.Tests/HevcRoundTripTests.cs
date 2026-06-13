using Agash.StreamTransport.Codecs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// Encodes with the machine's auto-selected hardware HEVC encoder then decodes with the hardware-first
/// decoder to prove the full codec round-trip (NVENC on Windows/Linux, VideoToolbox on macOS, rkmpp on
/// Rockchip). Skips where no hardware encoder is available.
/// </summary>
[TestClass]
[DoNotParallelize] // Drives a hardware HEVC encoder/decoder; one GPU session at a time.
public sealed class HevcRoundTripTests
{
    [TestMethod]
    public void EncodeThenDecode_Hevc_RecoversFrameDimensions()
    {
        string? nativeBin = TestNative.FindFFmpegBin();
        if (nativeBin is null)
        {
            Assert.Inconclusive("No bundled FFmpeg native build found; skipping HEVC round-trip test.");
            return;
        }

        FFmpegLibrary.EnsureLoaded(nativeBin);

        // Auto-select whatever raw-NV12 hardware HEVC encoder this machine has (VideoToolbox, rkmpp,
        // nvenc, qsv, amf). If none is in the build, or its GPU isn't usable here, skip.
        string encoderName;
        try
        {
            encoderName = HevcEncoderSelector.Select();
        }
        catch (NotSupportedException ex)
        {
            Assert.Inconclusive($"No hardware HEVC encoder available on this machine: {ex.Message}");
            return;
        }

        const int width = 1280;
        const int height = 720;
        HardwareHevcEncoder encoder;
        try
        {
            encoder = new HardwareHevcEncoder(encoderName, width, height, fps: 30, bitrate: 4_000_000);
        }
        catch (HardwareEncoderUnavailableException ex)
        {
            Assert.Inconclusive($"{encoderName} hardware is not available on this machine: {ex.Message}");
            return;
        }

        using (encoder)
        {
            using var decoder = new HevcDecoder();

            byte[] nv12 = HardwareEncoderTestSupport.Nv12Pattern(width, height);

            bool decodedAny = false;
            for (int frame = 0; frame < 30 && !decodedAny; frame++)
            {
                byte[]? accessUnit = encoder.EncodeNv12(nv12);
                if (accessUnit is null)
                {
                    continue;
                }

                if (decoder.Decode(accessUnit, 0, out int decW, out int decH, out byte[] pixels, out _, out _))
                {
                    Assert.AreEqual(width, decW, "Decoded width should match the encoded frame.");
                    Assert.AreEqual(height, decH, "Decoded height should match the encoded frame.");
                    Assert.AreEqual(width * height * 3 / 2, pixels.Length, "Decoded buffer should be a full 4:2:0 frame.");
                    decodedAny = true;
                }
            }

            Assert.IsTrue(decodedAny, "Expected at least one decoded frame from the HEVC round-trip.");

            // Where a hardware HEVC decoder is in the build (NVDEC), it must be the one that engaged.
            if (FFmpegLibrary.HasDecoder("hevc_cuvid"))
            {
                Assert.AreEqual("hevc_cuvid", decoder.DecoderName, "Hardware NVDEC decoder should be selected when available.");
            }
        }
    }
}
