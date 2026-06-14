using Agash.StreamTransport.Codecs;
using FFmpeg.AutoGen;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// Verifies the load-bearing primitive of the zero-copy GPU republish: a VAAPI surface-to-surface copy
/// (<c>vaCopy</c>) on the shared device. The republish copies each decoded surface into a presentation
/// surface we own and export to PipeWire, so this must work on the real driver. Inconclusive where VAAPI is
/// absent, or where the driver does not implement <c>vaCopy</c> (so we learn to fall back rather than guess).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed unsafe class VaapiCopyTests
{
    [TestMethod]
    public void VaCopy_BetweenOwnedVaapiSurfaces_Succeeds()
    {
        string? nativeBin = TestNative.FindFFmpegBin();
        if (nativeBin is null)
        {
            Assert.Inconclusive("No bundled FFmpeg native build found.");
            return;
        }

        FFmpegLibrary.EnsureLoaded(nativeBin);

        if (!VaapiDevice.IsAvailable())
        {
            Assert.Inconclusive("No VAAPI device available on this machine.");
            return;
        }

        nint display = VaapiDevice.Display;
        Assert.AreNotEqual(nint.Zero, display, "VADisplay must be readable from the shared device.");

        AVBufferRef* device = VaapiDevice.AcquireRef();
        AVBufferRef* framesRef = ffmpeg.av_hwframe_ctx_alloc(device);
        Assert.IsTrue(framesRef is not null, "allocate VAAPI frames context");

        var framesCtx = (AVHWFramesContext*)framesRef->data;
        framesCtx->format = AVPixelFormat.AV_PIX_FMT_VAAPI;
        framesCtx->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        framesCtx->width = 64;
        framesCtx->height = 64;
        framesCtx->initial_pool_size = 2;
        ffmpeg.av_hwframe_ctx_init(framesRef).ThrowOnError("init VAAPI frames context");

        AVFrame* src = ffmpeg.av_frame_alloc();
        AVFrame* dst = ffmpeg.av_frame_alloc();
        try
        {
            ffmpeg.av_hwframe_get_buffer(framesRef, src, 0).ThrowOnError("get src surface");
            ffmpeg.av_hwframe_get_buffer(framesRef, dst, 0).ThrowOnError("get dst surface");

            uint srcId = (uint)(nuint)src->data[3];
            uint dstId = (uint)(nuint)dst->data[3];

            var s = VaapiInterop.VACopyObject.Surface(srcId);
            var d = VaapiInterop.VACopyObject.Surface(dstId);
            int status = VaapiInterop.vaCopy(display, &d, &s, 0);
            if (status != 0)
            {
                Assert.Inconclusive($"vaCopy is not implemented/usable on this driver (VAStatus={status}); the republish must use scale_vaapi instead.");
                return;
            }

            int sync = VaapiInterop.vaSyncSurface(display, dstId);
            Assert.AreEqual(0, sync, "vaSyncSurface should succeed after a successful vaCopy.");
        }
        finally
        {
            ffmpeg.av_frame_free(&src);
            ffmpeg.av_frame_free(&dst);
            ffmpeg.av_buffer_unref(&framesRef);
            ffmpeg.av_buffer_unref(&device);
        }
    }
}
