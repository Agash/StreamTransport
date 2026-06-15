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

    [TestMethod]
    public void VaVpp_CopyBetweenOwnedSurfaces_Succeeds()
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
        Assert.AreNotEqual(nint.Zero, display);

        AVBufferRef* device = VaapiDevice.AcquireRef();
        AVBufferRef* framesRef = ffmpeg.av_hwframe_ctx_alloc(device);
        var framesCtx = (AVHWFramesContext*)framesRef->data;
        framesCtx->format = AVPixelFormat.AV_PIX_FMT_VAAPI;
        framesCtx->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        framesCtx->width = 64;
        framesCtx->height = 64;
        framesCtx->initial_pool_size = 2;
        ffmpeg.av_hwframe_ctx_init(framesRef).ThrowOnError("init VAAPI frames context");

        AVFrame* src = ffmpeg.av_frame_alloc();
        AVFrame* dst = ffmpeg.av_frame_alloc();
        uint config = 0, context = 0, paramBuf = 0;
        try
        {
            ffmpeg.av_hwframe_get_buffer(framesRef, src, 0).ThrowOnError("get src surface");
            ffmpeg.av_hwframe_get_buffer(framesRef, dst, 0).ThrowOnError("get dst surface");
            uint srcId = (uint)(nuint)src->data[3];
            uint dstId = (uint)(nuint)dst->data[3];

            int cfg = VaapiInterop.vaCreateConfig(display, VaapiInterop.VAProfileNone, VaapiInterop.VAEntrypointVideoProc, null, 0, out config);
            if (cfg != 0)
            {
                Assert.Inconclusive($"VAEntrypointVideoProc not supported on this driver (VAStatus={cfg}).");
                return;
            }

            uint renderTarget = dstId;
            VaapiInterop.vaCreateContext(display, config, 64, 64, VaapiInterop.VA_PROGRESSIVE, &renderTarget, 1, out context).ThrowOnError("vaCreateContext (VPP)");

            // Whole-surface copy: zero the 224-byte pipeline param, set only the source surface (offset 0).
            byte* param = stackalloc byte[VaapiInterop.ProcPipelineParameterBufferSize];
            new Span<byte>(param, VaapiInterop.ProcPipelineParameterBufferSize).Clear();
            *(uint*)param = srcId;
            VaapiInterop.vaCreateBuffer(display, context, VaapiInterop.VAProcPipelineParameterBufferType,
                VaapiInterop.ProcPipelineParameterBufferSize, 1, param, out paramBuf).ThrowOnError("vaCreateBuffer");

            VaapiInterop.vaBeginPicture(display, context, dstId).ThrowOnError("vaBeginPicture");
            uint b = paramBuf;
            VaapiInterop.vaRenderPicture(display, context, &b, 1).ThrowOnError("vaRenderPicture");
            VaapiInterop.vaEndPicture(display, context).ThrowOnError("vaEndPicture");

            int sync = VaapiInterop.vaSyncSurface(display, dstId);
            Assert.AreEqual(0, sync, "vaSyncSurface should succeed after a VPP copy.");
        }
        finally
        {
            if (paramBuf != 0) VaapiInterop.vaDestroyBuffer(display, paramBuf);
            if (context != 0) VaapiInterop.vaDestroyContext(display, context);
            if (config != 0) VaapiInterop.vaDestroyConfig(display, config);
            ffmpeg.av_frame_free(&src);
            ffmpeg.av_frame_free(&dst);
            ffmpeg.av_buffer_unref(&framesRef);
            ffmpeg.av_buffer_unref(&device);
        }
    }

    [TestMethod]
    public void PresentationPool_ExportsDmaBuf_AndVppCopies()
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

        using var pool = new VaapiPresentationPool(1280, 720, count: 3);
        Assert.AreEqual(3, pool.Count);

        for (int i = 0; i < pool.Count; i++)
        {
            DmaBufSurface planes = pool.Planes(i);
            Assert.IsTrue(planes.PlaneCount is 1 or 2, $"surface {i} should export 1-2 NV12 planes, got {planes.PlaneCount}.");
            for (int p = 0; p < planes.PlaneCount; p++)
            {
                Assert.IsTrue(planes[p].Fd > 0, $"surface {i} plane {p} must have a valid dmabuf fd.");
                Assert.IsTrue(planes[p].Stride > 0, $"surface {i} plane {p} must have a positive stride.");
            }

            Assert.AreNotEqual(0u, pool.SurfaceId(i));
        }

        // GPU-copy pool surface 0 into pool surface 1 and 2 - exercises the VPP path against pre-declared
        // render targets (every pool surface is a target), the exact operation the republish runs per frame.
        Assert.AreEqual(0, pool.CopyInto(pool.SurfaceId(0), 1), "VPP copy 0->1 should succeed.");
        Assert.AreEqual(0, pool.CopyInto(pool.SurfaceId(0), 2), "VPP copy 0->2 should succeed.");
    }
}
