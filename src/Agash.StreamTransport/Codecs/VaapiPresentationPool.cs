using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// A fixed pool of VAAPI NV12 surfaces we own, each exported once to a stable DMA-BUF, with a VA-API VPP
/// context to copy any source surface into any pool surface on the GPU (no CPU readback). This is the
/// zero-copy republish target: the decoder's surfaces are recycled per frame and unsuitable for PipeWire
/// (which sends a buffer's dmabuf fds to the consumer once, at <c>add_buffer</c>), so each frame we VPP-copy
/// the decoded surface into one of these stable, pre-exported surfaces and publish its dmabuf.
///
/// <para>VPP (<c>VAEntrypointVideoProc</c>) is the copy mechanism because radeonsi does not implement
/// <c>vaCopy</c>; the copy is a GPU blit, so nothing ever touches the CPU. All surfaces are created up front
/// and registered as the VPP context's render targets so any of them may be a copy destination.</para>
/// </summary>
/// <remarks>Linux/VAAPI only. Construct on a thread that holds no PipeWire loop lock; copies may run on the loop thread.</remarks>
public sealed unsafe class VaapiPresentationPool : IDisposable
{
    private readonly nint _display;
    private AVBufferRef* _device;
    private AVBufferRef* _framesRef;
    private readonly AVFrame*[] _surfaces;
    private readonly AVFrame*[] _drmMaps;
    private readonly DmaBufSurface[] _planes;
    private readonly uint[] _surfaceIds;
    private uint _vppConfig;
    private uint _vppContext;
    private bool _disposed;

    // Keep in step with DmaBufSurface's inline-array capacity (4); a flat NV12 export uses 2.
    private const int MaxPlanes = 4;

    /// <summary>Allocate a VAAPI presentation pool of <paramref name="count"/> surfaces of the given size.</summary>
    /// <param name="width">Surface width in pixels.</param>
    /// <param name="height">Surface height in pixels.</param>
    /// <param name="count">Number of render-target surfaces to allocate up front.</param>
    public VaapiPresentationPool(int width, int height, int count)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("VAAPI presentation pool is Linux-only.");
        }

        Count = count;
        _surfaces = new AVFrame*[count];
        _drmMaps = new AVFrame*[count];
        _planes = new DmaBufSurface[count];
        _surfaceIds = new uint[count];

        _display = VaapiDevice.Display;
        if (_display == 0)
        {
            throw new NotSupportedException("No VAAPI display available for the presentation pool.");
        }

        _device = VaapiDevice.AcquireRef();
        _framesRef = ffmpeg.av_hwframe_ctx_alloc(_device);
        if (_framesRef is null)
        {
            throw new NotSupportedException("Failed to allocate VAAPI frames context for the presentation pool.");
        }

        var framesCtx = (AVHWFramesContext*)_framesRef->data;
        framesCtx->format = AVPixelFormat.AV_PIX_FMT_VAAPI;
        framesCtx->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        framesCtx->width = width;
        framesCtx->height = height;
        framesCtx->initial_pool_size = count;
        ffmpeg.av_hwframe_ctx_init(_framesRef).ThrowOnError("init presentation frames context");

        for (int i = 0; i < count; i++)
        {
            AVFrame* surface = ffmpeg.av_frame_alloc();
            ffmpeg.av_hwframe_get_buffer(_framesRef, surface, 0).ThrowOnError("allocate presentation surface");
            _surfaces[i] = surface;
            _surfaceIds[i] = (uint)(nuint)surface->data[3];
            _planes[i] = ExportDmaBuf(surface, i);
        }

        CreateVppContext();
    }

    /// <summary>The number of surfaces (and thus PipeWire buffers) in the pool.</summary>
    public int Count { get; }

    /// <summary>The stable DMA-BUF plane layout of pool surface <paramref name="index"/> (for PipeWire add_buffer).</summary>
    public DmaBufSurface Planes(int index) => _planes[index];

    /// <summary>The DRM format modifier the pool surfaces were allocated with (uniform across the pool).</summary>
    public ulong Modifier => Count > 0 ? _planes[0].Modifier : 0;

    /// <summary>The VA surface id of pool surface <paramref name="index"/>.</summary>
    public uint SurfaceId(int index) => _surfaceIds[index];

    /// <summary>
    /// GPU-copy <paramref name="sourceSurfaceId"/> into pool surface <paramref name="destIndex"/> via VA-API VPP
    /// and wait for it to complete. No CPU transfer. Returns the VA status (0 = success).
    /// </summary>
    public int CopyInto(uint sourceSurfaceId, int destIndex)
    {
        uint paramBuf = 0;
        try
        {
            // Whole-surface copy: zero the pipeline param, set only the source surface (offset 0); the dst is
            // the vaBeginPicture render target. Everything else zero = whole surface, default colour.
            byte* param = stackalloc byte[VaapiInterop.ProcPipelineParameterBufferSize];
            new Span<byte>(param, VaapiInterop.ProcPipelineParameterBufferSize).Clear();
            *(uint*)param = sourceSurfaceId;

            int rc = VaapiInterop.vaCreateBuffer(_display, _vppContext, VaapiInterop.VAProcPipelineParameterBufferType,
                VaapiInterop.ProcPipelineParameterBufferSize, 1, param, out paramBuf);
            if (rc != 0)
            {
                return rc;
            }

            rc = VaapiInterop.vaBeginPicture(_display, _vppContext, _surfaceIds[destIndex]);
            if (rc != 0)
            {
                return rc;
            }

            uint b = paramBuf;
            rc = VaapiInterop.vaRenderPicture(_display, _vppContext, &b, 1);
            if (rc != 0)
            {
                return rc;
            }

            rc = VaapiInterop.vaEndPicture(_display, _vppContext);
            if (rc != 0)
            {
                return rc;
            }

            return VaapiInterop.vaSyncSurface(_display, _surfaceIds[destIndex]);
        }
        finally
        {
            if (paramBuf != 0)
            {
                VaapiInterop.vaDestroyBuffer(_display, paramBuf);
            }
        }
    }

    private void CreateVppContext()
    {
        VaapiInterop.vaCreateConfig(_display, VaapiInterop.VAProfileNone, VaapiInterop.VAEntrypointVideoProc, null, 0, out _vppConfig)
            .ThrowOnError("create VPP config");

        // Register every pool surface as a render target so any of them can be a copy destination.
        fixed (uint* targets = _surfaceIds)
        {
            VaapiInterop.vaCreateContext(_display, _vppConfig,
                ((AVHWFramesContext*)_framesRef->data)->width, ((AVHWFramesContext*)_framesRef->data)->height,
                VaapiInterop.VA_PROGRESSIVE, targets, Count, out _vppContext)
                .ThrowOnError("create VPP context");
        }
    }

    // Maps an owned VAAPI surface to a DRM-PRIME descriptor once and keeps the mapping alive for the surface's
    // lifetime (the exported fds must stay valid as long as PipeWire holds the buffer). Flattens to a flat
    // DmaBufSurface preserving each layer's DRM fourcc, mirroring VaapiVideoDecoder.TryExportDmaBuf.
    private DmaBufSurface ExportDmaBuf(AVFrame* surface, int index)
    {
        AVFrame* drm = ffmpeg.av_frame_alloc();
        drm->format = (int)AVPixelFormat.AV_PIX_FMT_DRM_PRIME;
        // Map for read+write: the surface is both a VPP destination (write) and a PipeWire source (read).
        if (ffmpeg.av_hwframe_map(drm, surface, (int)DrmPrime.HwframeMapRead | (int)DrmPrime.HwframeMapWrite) < 0 || drm->data[0] is null)
        {
            ffmpeg.av_frame_free(&drm);
            throw new NotSupportedException("Failed to export presentation surface to DMA-BUF.");
        }

        ref readonly AVDRMFrameDescriptor desc = ref *(AVDRMFrameDescriptor*)drm->data[0];
        ulong modifier = desc.objects[0].format_modifier;
        Span<DmaBufPlane> planes = stackalloc DmaBufPlane[MaxPlanes];
        int planeCount = 0;
        for (int l = 0; l < desc.nb_layers && planeCount < planes.Length; l++)
        {
            ref readonly AVDRMLayerDescriptor layer = ref desc.layers[l];
            for (int p = 0; p < layer.nb_planes && planeCount < planes.Length; p++)
            {
                ref readonly AVDRMPlaneDescriptor plane = ref layer.planes[p];
                int fd = desc.objects[plane.object_index].fd;
                planes[planeCount++] = new DmaBufPlane(fd, (uint)plane.offset, (uint)plane.pitch, layer.format);
            }
        }

        if (planeCount == 0)
        {
            ffmpeg.av_frame_free(&drm);
            throw new NotSupportedException("Presentation surface exported zero planes.");
        }

        // Hold the mapping for the surface's lifetime so the fds stay valid (do not free drm here).
        _drmMaps[index] = drm;
        return new DmaBufSurface(modifier, VideoPixelFormat.Nv12, planes[..planeCount]);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_vppContext != 0)
        {
            VaapiInterop.vaDestroyContext(_display, _vppContext);
            _vppContext = 0;
        }

        if (_vppConfig != 0)
        {
            VaapiInterop.vaDestroyConfig(_display, _vppConfig);
            _vppConfig = 0;
        }

        for (int i = 0; i < Count; i++)
        {
            if (_drmMaps[i] is not null)
            {
                fixed (AVFrame** drm = &_drmMaps[i])
                {
                    ffmpeg.av_frame_free(drm);
                }
            }

            if (_surfaces[i] is not null)
            {
                fixed (AVFrame** s = &_surfaces[i])
                {
                    ffmpeg.av_frame_free(s);
                }
            }
        }

        fixed (AVBufferRef** frames = &_framesRef)
        {
            ffmpeg.av_buffer_unref(frames);
        }

        fixed (AVBufferRef** device = &_device)
        {
            ffmpeg.av_buffer_unref(device);
        }
    }
}
