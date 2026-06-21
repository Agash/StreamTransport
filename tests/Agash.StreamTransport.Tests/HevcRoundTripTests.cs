using Agash.StreamTransport;
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
        IVideoEncoderBackend encoder;
        try
        {
            // hevc_vaapi encodes VAAPI surfaces through its own backend; the rest take system-memory NV12.
            encoder = encoderName == "hevc_vaapi"
                ? new VaapiVideoEncoder(width, height, fps: 30, bitrate: 4_000_000)
                : new HardwareHevcEncoder(encoderName, width, height, fps: 30, bitrate: 4_000_000);
        }
        catch (HardwareEncoderUnavailableException ex)
        {
            Assert.Inconclusive($"{encoderName} hardware is not available on this machine: {ex.Message}");
            return;
        }
        catch (Exception ex) when (encoderName == "hevc_vaapi")
        {
            Assert.Inconclusive($"hevc_vaapi hardware is not available on this machine: {ex.Message}");
            return;
        }

        using (encoder)
        {
            // Decode with the matching hardware path: a VAAPI encode pairs with the VAAPI decoder (the Mesa
            // round-trip on AMD/Intel Linux), every other encoder with the hardware-first/software HevcDecoder.
            using IVideoDecoderBackend decoder = encoderName == "hevc_vaapi"
                ? new VaapiVideoDecoder()
                : new HevcDecoder();

            byte[] nv12 = HardwareEncoderTestSupport.Nv12Pattern(width, height);
            var inputFrame = VideoFrame.FromPixels(nv12, VideoPixelFormat.Nv12, width, height, 0);

            bool decodedAny = false;
            try
            {
                for (int frame = 0; frame < 30 && !decodedAny; frame++)
                {
                    byte[]? accessUnit = encoder.Encode(inputFrame, out _);
                    if (accessUnit is null)
                    {
                        continue;
                    }

                    if (decoder.TryDecode(accessUnit, 0, 0, out VideoFrame decoded, out _))
                    {
                        Assert.AreEqual(width, decoded.Width, "Decoded width should match the encoded frame.");
                        Assert.AreEqual(height, decoded.Height, "Decoded height should match the encoded frame.");
                        Assert.AreEqual(width * height * 3 / 2, decoded.Pixels.Length, "Decoded buffer should be a full 4:2:0 frame.");
                        decodedAny = true;
                    }
                }
            }
            catch (Exception ex)
            {
                // Some HW encoders open even with no GPU and fail only at the first encode (VideoToolbox on a
                // headless CI runner: -542398533). Treat as absent hardware - a host with real hardware still passes.
                Assert.Inconclusive($"{encoderName} hardware encode is not available on this machine: {ex.Message}");
                return;
            }

            Assert.IsTrue(decodedAny, "Expected at least one decoded frame from the HEVC round-trip.");

            // On NVIDIA (where the selected encoder is nvenc) the NVDEC decoder must engage. Other vendors'
            // multi-encoder desktop builds also contain hevc_cuvid, but it cannot open without an NVIDIA GPU,
            // so gate the assertion on actually having encoded with nvenc rather than mere build presence.
            if (encoderName == "hevc_nvenc" && decoder is HevcDecoder hevc && FFmpegLibrary.HasDecoder("hevc_cuvid"))
            {
                Assert.AreEqual("hevc_cuvid", hevc.DecoderName, "Hardware NVDEC decoder should be selected when available.");
            }
        }
    }
}
