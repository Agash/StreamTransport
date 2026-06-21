using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// A hardware HEVC encoder for Linux Intel/AMD via VAAPI. Unlike nvenc (which ingests CPU NV12 directly),
/// <c>hevc_vaapi</c> only encodes VAAPI surfaces, so this encoder owns a VAAPI device and frames pool and
/// uploads each NV12 frame into a surface before encoding. The same device can later import a PipeWire
/// DMA-BUF as a surface for a fully zero-copy capture path (DRM-PRIME map); this CPU-upload path is the
/// baseline that makes Linux Intel/AMD hardware encode work at all. Base-TFM, gated to Linux at runtime.
/// </summary>
/// <remarks>Compiles everywhere (FFmpeg.AutoGen + P/Invoke); only functional on Linux with a VAAPI driver.</remarks>
internal sealed unsafe class VaapiVideoEncoder : IDisposable, IVideoEncoderBackend
{
    /// <summary>VAAPI uploads NV12 from CPU internally; no shared GPU device handle is surfaced.</summary>
    public nint NativeDevice => 0;

    /// <inheritdoc/>
    public byte[]? Encode(in VideoFrame frame, out long capturePtsNs)
    {
        // Zero-copy path: a DMA-BUF surface (from PipeWire capture or a Vulkan pack) is imported straight
        // into a VAAPI surface and encoded - no CPU upload. Otherwise upload the CPU NV12 buffer.
        if (frame.InteropKind == StreamInteropKind.PipeWire && frame.DmaBuf.HasValue)
        {
            return EncodeDmaBuf(frame.DmaBuf.Value, frame.Width, frame.Height, frame.PresentationTimeNs, frame.ForceKeyframe, out capturePtsNs);
        }

        return EncodeNv12(frame.Pixels.ToArray(), frame.Width, frame.Height, frame.PresentationTimeNs, frame.ForceKeyframe, out capturePtsNs);
    }

    private AVCodecContext* _context;
    private AVBufferRef* _hwDevice;
    private AVBufferRef* _hwFrames;
    private AVPacket* _packet;
    private AVFrame* _swFrame;
    private bool _disposed;

    public VaapiVideoEncoder(int width, int height, int fps, long bitrate, MediaProfile profile = MediaProfile.InteractiveP2P, string? renderNode = null)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("VAAPI encoding is only available on Linux.");
        }

        AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name("hevc_vaapi");
        if (codec is null)
        {
            throw new NotSupportedException("hevc_vaapi is not present in this FFmpeg build.");
        }

        // Share the one process-wide VAAPI device (see VaapiDevice) rather than opening a private VADisplay -
        // opening/closing several in a process is unstable on the Mesa radeonsi VA driver.
        _hwDevice = VaapiDevice.AcquireRef(renderNode);

        _hwFrames = ffmpeg.av_hwframe_ctx_alloc(_hwDevice);
        var frames = (AVHWFramesContext*)_hwFrames->data;
        frames->format = AVPixelFormat.AV_PIX_FMT_VAAPI;
        frames->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        frames->width = width;
        frames->height = height;
        frames->initial_pool_size = 8;
        ffmpeg.av_hwframe_ctx_init(_hwFrames).ThrowOnError("init VAAPI frames pool");

        _context = ffmpeg.avcodec_alloc_context3(codec);
        _context->width = width;
        _context->height = height;
        _context->time_base = new AVRational { num = 1, den = fps };
        _context->framerate = new AVRational { num = fps, den = 1 };
        _context->bit_rate = bitrate;
        _context->gop_size = fps * 2;
        _context->max_b_frames = 0;
        _context->pix_fmt = AVPixelFormat.AV_PIX_FMT_VAAPI;
        _context->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFrames);
        _context->colorspace = AVColorSpace.AVCOL_SPC_BT709;
        _context->color_range = AVColorRange.AVCOL_RANGE_MPEG;

        LowLatencyEncoderOptions.ConfigureContext(_context, profile, bitrate, fps);
        AVDictionary* options = null;
        LowLatencyEncoderOptions.Apply(&options, "hevc_vaapi", profile);
        int open = ffmpeg.avcodec_open2(_context, codec, &options);
        ffmpeg.av_dict_free(&options);
        open.ThrowOnError("open hevc_vaapi");

        _packet = ffmpeg.av_packet_alloc();
        _swFrame = ffmpeg.av_frame_alloc();
        _swFrame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
        _swFrame->width = width;
        _swFrame->height = height;
        ffmpeg.av_frame_get_buffer(_swFrame, 32).ThrowOnError("allocate NV12 staging frame");
    }

    /// <summary>Upload an NV12 buffer into a VAAPI surface and encode it. Returns the HEVC access unit, or null.</summary>
    public byte[]? EncodeNv12(byte[] nv12, int width, int height) =>
        EncodeNv12(nv12, width, height, 0, forceKeyframe: false, out _);

    public byte[]? EncodeNv12(byte[] nv12, int width, int height, long capturePtsNs, bool forceKeyframe, out long producedPtsNs)
    {
        producedPtsNs = 0;
        ffmpeg.av_frame_make_writable(_swFrame).ThrowOnError("make staging frame writable");
        CopyNv12(nv12, width, height);

        AVFrame* hwFrame = ffmpeg.av_frame_alloc();
        try
        {
            ffmpeg.av_hwframe_get_buffer(_hwFrames, hwFrame, 0).ThrowOnError("acquire VAAPI surface");
            ffmpeg.av_hwframe_transfer_data(hwFrame, _swFrame, 0).ThrowOnError("upload NV12 to VAAPI surface");
            hwFrame->pts = capturePtsNs;
            hwFrame->pict_type = forceKeyframe ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;

            ffmpeg.avcodec_send_frame(_context, hwFrame).ThrowOnError("send frame to hevc_vaapi");
            ffmpeg.av_packet_unref(_packet);
            int receive = ffmpeg.avcodec_receive_packet(_context, _packet);
            if (receive == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receive == ffmpeg.AVERROR_EOF)
            {
                return null;
            }

            receive.ThrowOnError("receive encoded packet");
            producedPtsNs = _packet->pts != ffmpeg.AV_NOPTS_VALUE ? _packet->pts : capturePtsNs;
            byte[] output = new byte[_packet->size];
            Marshal.Copy((nint)_packet->data, output, 0, _packet->size);
            return output;
        }
        finally
        {
            ffmpeg.av_frame_free(&hwFrame);
        }
    }

    // Import a DMA-BUF surface straight into a VAAPI surface and encode it - no CPU upload. The dmabuf is
    // wrapped as an AV_PIX_FMT_DRM_PRIME frame (its hand-defined AVDRMFrameDescriptor in data[0]) carried by
    // a DRM frames context derived from this encoder's VAAPI device, then av_hwframe_map'd (DIRECT) into a
    // VAAPI surface that aliases the same memory. The caller owns the source fds (we never close them).
    private byte[]? EncodeDmaBuf(in DmaBufSurface surface, int width, int height, long capturePtsNs, bool forceKeyframe, out long producedPtsNs)
    {
        producedPtsNs = 0;
        FfmpegLog.InstallIfRequested();

        // Rebuild the producer's exact DRM-PRIME descriptor: one layer per plane carrying the plane's own DRM
        // fourcc (preserved verbatim from the producer - e.g. R8 for Y, GR88 for UV on this Mesa stack), with
        // distinct fds collapsed into objects. No per-format guessing: whatever the producer emitted is fed
        // back unchanged, so this works for any driver's plane representation, not just one.
        AVDRMFrameDescriptor desc = default;
        Span<int> objectFds = stackalloc int[DrmPrime.MaxPlanes];
        int objectCount = 0;
        int planeCount = Math.Min(surface.PlaneCount, DrmPrime.MaxPlanes);
        for (int l = 0; l < planeCount; l++)
        {
            DmaBufPlane plane = surface[l];
            int objectIndex = -1;
            for (int o = 0; o < objectCount; o++)
            {
                if (objectFds[o] == plane.Fd) { objectIndex = o; break; }
            }

            if (objectIndex < 0)
            {
                objectIndex = objectCount;
                objectFds[objectCount] = plane.Fd;
                desc.objects[objectCount] = new AVDRMObjectDescriptor
                {
                    fd = plane.Fd,
                    size = 0,
                    format_modifier = surface.Modifier,
                };
                objectCount++;
            }

            var layer = new AVDRMLayerDescriptor { format = plane.DrmFourcc, nb_planes = 1 };
            layer.planes[0] = new AVDRMPlaneDescriptor
            {
                object_index = objectIndex,
                offset = (nint)plane.Offset,
                pitch = (nint)plane.Stride,
            };
            desc.layers[l] = layer;
        }

        desc.nb_objects = objectCount;
        desc.nb_layers = planeCount;

        AVFrame* drm = ffmpeg.av_frame_alloc();
        AVFrame* va = ffmpeg.av_frame_alloc();
        // A DRM-PRIME frame carries its AVDRMFrameDescriptor in buf[0] (an AVBufferRef), with data[0]
        // pointing at it - this is how FFmpeg's own DRM frame pool builds frames, and av_hwframe_map needs it
        // there, not merely in data[0]. Allocated per call; av_frame_free releases it.
        AVBufferRef* descBuf = ffmpeg.av_buffer_alloc((ulong)sizeof(AVDRMFrameDescriptor));
        try
        {
            *(AVDRMFrameDescriptor*)descBuf->data = desc;

            // No frames context on the source: av_hwframe_map tries the source's map_from first, and DRM's
            // map_from only maps to software formats - for a VAAPI destination it returns EINVAL and the
            // dispatch aborts. With no source frames context that branch is skipped and the destination VAAPI
            // context's map_to runs (-> vaapi_map_from_drm), creating a VA surface aliasing the dmabuf. The
            // destination uses the encoder's own VAAPI frames context, so the codec already accepts the surface.
            drm->format = (int)AVPixelFormat.AV_PIX_FMT_DRM_PRIME;
            drm->width = width;
            drm->height = height;
            drm->buf[0] = descBuf;
            drm->data[0] = descBuf->data;

            va->format = (int)AVPixelFormat.AV_PIX_FMT_VAAPI;
            va->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFrames);
            ffmpeg.av_hwframe_map(va, drm, (int)AV_HWFRAME_MAP_READ)
                .ThrowOnError("map DMA-BUF into VAAPI surface");
            va->pts = capturePtsNs;
            va->pict_type = forceKeyframe ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;

            ffmpeg.avcodec_send_frame(_context, va).ThrowOnError("send dmabuf surface to hevc_vaapi");
            ffmpeg.av_packet_unref(_packet);
            int receive = ffmpeg.avcodec_receive_packet(_context, _packet);
            if (receive == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receive == ffmpeg.AVERROR_EOF)
            {
                return null;
            }

            receive.ThrowOnError("receive encoded packet");
            producedPtsNs = _packet->pts != ffmpeg.AV_NOPTS_VALUE ? _packet->pts : capturePtsNs;
            byte[] output = new byte[_packet->size];
            Marshal.Copy((nint)_packet->data, output, 0, _packet->size);
            return output;
        }
        finally
        {
            ffmpeg.av_frame_free(&va);
            ffmpeg.av_frame_free(&drm);
        }
    }

    // av_hwframe_map(DRM_PRIME source -> VAAPI destination, READ) creates a VA surface aliasing the dmabuf
    // (vaapi map_to), no copy. Read access: the encoder only reads the surface.
    private const uint AV_HWFRAME_MAP_READ = 1;

    private void CopyNv12(byte[] nv12, int width, int height)
    {
        // Y plane, then interleaved UV plane, respecting the staging frame's line size.
        int yStride = _swFrame->linesize[0];
        for (int row = 0; row < height; row++)
        {
            Marshal.Copy(nv12, row * width, (nint)(_swFrame->data[0] + (row * yStride)), width);
        }

        int uvStride = _swFrame->linesize[1];
        int uvOffset = width * height;
        for (int row = 0; row < height / 2; row++)
        {
            Marshal.Copy(nv12, uvOffset + (row * width), (nint)(_swFrame->data[1] + (row * uvStride)), width);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        fixed (AVPacket** packet = &_packet)
        {
            ffmpeg.av_packet_free(packet);
        }

        fixed (AVFrame** frame = &_swFrame)
        {
            ffmpeg.av_frame_free(frame);
        }

        fixed (AVCodecContext** context = &_context)
        {
            ffmpeg.avcodec_free_context(context);
        }

        fixed (AVBufferRef** frames = &_hwFrames)
        {
            ffmpeg.av_buffer_unref(frames);
        }

        fixed (AVBufferRef** device = &_hwDevice)
        {
            ffmpeg.av_buffer_unref(device);
        }
    }
}
