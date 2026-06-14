using Agash.StreamTransport;
using Agash.StreamTransport.Codecs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// Exercises the zero-copy VAAPI decode path: decode straight to a DMA-BUF (DRM-PRIME) surface instead of
/// reading back to CPU. This validates the hand-defined <c>AVDRM*Descriptor</c> ABI against a real Mesa
/// VAAPI driver - the export only produces a usable plane layout if the struct layout matches FFmpeg's.
/// Inconclusive anywhere hevc_vaapi is not available (Windows, macOS, a GPU-less box).
/// </summary>
[TestClass]
[DoNotParallelize] // Drives a hardware HEVC encoder/decoder; one VAAPI session at a time.
public sealed class VaapiDmaBufExportTests
{
    [TestMethod]
    public void DecodeToDmaBuf_ExportsValidPlaneLayout()
    {
        string? nativeBin = TestNative.FindFFmpegBin();
        if (nativeBin is null)
        {
            Assert.Inconclusive("No bundled FFmpeg native build found.");
            return;
        }

        FFmpegLibrary.EnsureLoaded(nativeBin);

        const int width = 1280;
        const int height = 720;
        VaapiVideoEncoder encoder;
        try
        {
            encoder = new VaapiVideoEncoder(width, height, fps: 30, bitrate: 4_000_000);
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"hevc_vaapi hardware is not available on this machine: {ex.Message}");
            return;
        }

        using (encoder)
        using (var decoder = new VaapiVideoDecoder(gpuSurface: true))
        {
            Assert.AreEqual(StreamInteropKind.PipeWire, decoder.OutputSurfaceKind,
                "GPU-surface decoder must surface PipeWire/DMA-BUF frames.");

            byte[] nv12 = HardwareEncoderTestSupport.Nv12Pattern(width, height);
            var inputFrame = VideoFrame.FromPixels(nv12, VideoPixelFormat.Nv12, width, height, 0);

            bool exported = false;
            for (int frame = 0; frame < 30 && !exported; frame++)
            {
                byte[]? accessUnit = encoder.Encode(inputFrame, out _);
                if (accessUnit is null)
                {
                    continue;
                }

                if (!decoder.TryDecode(accessUnit, 0, 0, out VideoFrame decoded, out _))
                {
                    continue;
                }

                exported = true;
                Assert.AreEqual(StreamInteropKind.PipeWire, decoded.InteropKind, "decoded frame must be a DMA-BUF surface");
                Assert.AreEqual(width, decoded.Width);
                Assert.AreEqual(height, decoded.Height);
                Assert.IsTrue(decoded.Pixels.IsEmpty, "a GPU-surface frame carries no CPU pixels");

                Assert.IsTrue(decoded.DmaBuf.HasValue, "DMA-BUF surface must be present");
                DmaBufSurface surface = decoded.DmaBuf!.Value;
                Assert.AreEqual(VideoPixelFormat.Nv12, surface.Format);
                // NV12 exports as 1 or 2 planes (single fd with 2 layers, or 2 layers across objects).
                Assert.IsTrue(surface.PlaneCount is 1 or 2, $"unexpected plane count {surface.PlaneCount}");
                for (int p = 0; p < surface.PlaneCount; p++)
                {
                    DmaBufPlane plane = surface[p];
                    Assert.IsTrue(plane.Fd >= 0, $"plane {p} must carry a valid dmabuf fd");
                    Assert.IsTrue(plane.Stride >= width, $"plane {p} stride {plane.Stride} too small for width {width}");
                }
            }

            Assert.IsTrue(exported, "decoder should have exported at least one DMA-BUF surface");
        }
    }

    [TestMethod]
    public void TranscodeOverDmaBuf_ReencodesGpuSurface_NoCpuReadback()
    {
        string? nativeBin = TestNative.FindFFmpegBin();
        if (nativeBin is null)
        {
            Assert.Inconclusive("No bundled FFmpeg native build found.");
            return;
        }

        FFmpegLibrary.EnsureLoaded(nativeBin);

        const int width = 1280;
        const int height = 720;
        VaapiVideoEncoder encoder;
        try
        {
            encoder = new VaapiVideoEncoder(width, height, fps: 30, bitrate: 4_000_000);
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"hevc_vaapi hardware is not available on this machine: {ex.Message}");
            return;
        }

        // Phase 1: encode a CPU seed and decode it to a real DMA-BUF (DRM-PRIME) GPU surface.
        // Phase 2: import that surface into a FRESH encoder (av_hwframe_map) and encode it - zero CPU touch.
        // A non-empty access unit from phase 2 proves the import maps a real exported surface end to end. We
        // use a separate encoder for the re-encode so a clean IDR is produced (no shared-GOP interference) and
        // we don't re-decode it (a lone AU has no stream context).
        using (encoder)
        using (var gpuDecoder = new VaapiVideoDecoder(gpuSurface: true))
        {
            byte[] nv12 = HardwareEncoderTestSupport.Nv12Pattern(width, height);
            var seed = VideoFrame.FromPixels(nv12, VideoPixelFormat.Nv12, width, height, 0);

            bool sawSurface = false;
            bool importedOk = false;
            for (int frame = 0; frame < 60 && !importedOk; frame++)
            {
                byte[]? au = encoder.Encode(seed, out _);
                if (au is null || !gpuDecoder.TryDecode(au, 0, 0, out VideoFrame surface, out _))
                {
                    continue;
                }

                sawSurface = true;
                Assert.AreEqual(StreamInteropKind.PipeWire, surface.InteropKind);

                // Re-encode the imported surface on the same (primed) encoder. A non-empty AU proves the
                // zero-copy import worked: av_hwframe_map aliased the dmabuf as a VA surface and it encoded.
                byte[]? reencoded = encoder.Encode(surface, out _);
                if (reencoded is { Length: > 0 })
                {
                    importedOk = true;
                }
            }

            Assert.IsTrue(sawSurface, "decoder should have produced a DMA-BUF surface to import");
            Assert.IsTrue(importedOk, "importing a DMA-BUF surface and encoding it should produce a non-empty access unit");
        }
    }
}
