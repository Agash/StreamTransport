using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// Hardware HEVC decode on Linux Intel/AMD via VAAPI. FFmpeg decodes into VAAPI surfaces - the regular
/// <c>hevc</c> decoder driven by a VAAPI <c>hw_device_ctx</c> plus a <c>get_format</c> that selects
/// <c>AV_PIX_FMT_VAAPI</c> - and each decoded surface is transferred down to a CPU NV12 buffer for the
/// receive pipeline. This is the Linux mirror of the NVDEC/D3D11VA hardware decode paths; the hardware-first
/// list in <see cref="HevcDecoder"/> only covers NVDEC/QSV/rkmpp, which leaves AMD Mesa (where VAAPI is the
/// open path) on the software decoder. CPU-output backend (<see cref="StreamInteropKind.None"/>).
/// </summary>
/// <remarks>Compiles everywhere (FFmpeg.AutoGen); only functional on Linux with a VAAPI driver.</remarks>
internal sealed unsafe class VaapiVideoDecoder : IDisposable, IVideoDecoderBackend
{
    /// <summary>
    /// In GPU-surface mode the decoded VAAPI surface is exported as a DMA-BUF (DRM-PRIME) and surfaced as a
    /// <see cref="StreamInteropKind.PipeWire"/> frame for zero-copy downstream (Vulkan import / PipeWire
    /// republish); otherwise it is read back to CPU NV12 (<see cref="StreamInteropKind.None"/>).
    /// </summary>
    public StreamInteropKind OutputSurfaceKind => _gpuSurface ? StreamInteropKind.PipeWire : StreamInteropKind.None;

    /// <summary>No shared GPU device handle is surfaced (dmabuf carries its own fds).</summary>
    public nint NativeDevice => 0;

    private readonly bool _gpuSurface;
    private AVCodecContext* _context;
    private AVBufferRef* _hwDevice;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private AVFrame* _swFrame;
    private AVFrame* _drmFrame;
    private bool _disposed;

    /// <param name="renderNode">Optional DRM render node (e.g. <c>/dev/dri/renderD129</c>); first node by default.</param>
    /// <param name="gpuSurface">
    /// When <see langword="true"/>, export each decoded surface as a DMA-BUF instead of reading it back to
    /// CPU memory - the zero-copy path. Requires a downstream that consumes <see cref="StreamInteropKind.PipeWire"/>.
    /// </param>
    public VaapiVideoDecoder(string? renderNode = null, bool gpuSurface = false)
    {
        _gpuSurface = gpuSurface;
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("VAAPI decoding is only available on Linux.");
        }

        AVCodec* codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_HEVC);
        if (codec is null)
        {
            throw new NotSupportedException("No HEVC decoder available in the FFmpeg build.");
        }

        // Share the one process-wide VAAPI device (see VaapiDevice) rather than opening a private VADisplay -
        // opening/closing several in a process is unstable on the Mesa radeonsi VA driver.
        _hwDevice = VaapiDevice.AcquireRef(renderNode);

        _context = ffmpeg.avcodec_alloc_context3(codec);
        _context->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDevice);
        // Low-delay decode: the stream is zero-latency-encoded (no reordering), so emit each frame as soon as
        // it is decoded rather than holding a display-reorder buffer (cuts the receive-side A/V offset).
        _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        // 90 kHz RTP video clock, so a packet PTS set to the RTP timestamp passes through unscaled and the
        // produced frame reports the RTP timestamp of the access unit that made it (PTS propagation).
        _context->pkt_timebase = new AVRational { num = 1, den = 90_000 };
        _context->get_format = new AVCodecContext_get_format_func
        {
            Pointer = (nint)(delegate* unmanaged[Cdecl]<AVCodecContext*, AVPixelFormat*, AVPixelFormat>)&GetVaapiFormat,
        };

        int open = ffmpeg.avcodec_open2(_context, codec, null);
        if (open < 0)
        {
            // Release the device + half-open context before surfacing the failure so the factory's fallback
            // to software decode does not leak the VAAPI handles.
            fixed (AVCodecContext** context = &_context)
            {
                ffmpeg.avcodec_free_context(context);
            }

            fixed (AVBufferRef** dev = &_hwDevice)
            {
                ffmpeg.av_buffer_unref(dev);
            }

            open.ThrowOnError("open VAAPI HEVC decoder");
        }

        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        _swFrame = ffmpeg.av_frame_alloc();
        _drmFrame = ffmpeg.av_frame_alloc();
    }

    // get_format is called by FFmpeg with the list of formats the decoder can output for this stream; we pick
    // the VAAPI surface format so the hardware path engages (returning a software format here would silently
    // decode on the CPU instead).
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static AVPixelFormat GetVaapiFormat(AVCodecContext* context, AVPixelFormat* formats)
    {
        for (AVPixelFormat* p = formats; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if (*p == AVPixelFormat.AV_PIX_FMT_VAAPI)
            {
                return AVPixelFormat.AV_PIX_FMT_VAAPI;
            }
        }

        // No VAAPI format offered (unexpected for an opened VAAPI context); let FFmpeg use its first choice.
        return *formats;
    }

    /// <inheritdoc/>
    public bool TryDecode(ReadOnlySpan<byte> accessUnit, uint rtpTimestamp, long presentationTimeNs, out VideoFrame frame, out uint frameRtpTimestamp)
    {
        frame = default;
        frameRtpTimestamp = rtpTimestamp;

        ffmpeg.av_packet_unref(_packet);
        ffmpeg.av_new_packet(_packet, accessUnit.Length).ThrowOnError("allocate packet");
        accessUnit.CopyTo(new Span<byte>(_packet->data, accessUnit.Length));
        _packet->pts = rtpTimestamp;
        _packet->dts = rtpTimestamp;

        int sent = ffmpeg.avcodec_send_packet(_context, _packet);
        bool resend = sent == ffmpeg.AVERROR(ffmpeg.EAGAIN);
        if (!resend)
        {
            sent.ThrowOnError("send packet to VAAPI decoder");
        }

        int receive = ffmpeg.avcodec_receive_frame(_context, _frame);
        if (receive == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receive == ffmpeg.AVERROR_EOF)
        {
            return false;
        }

        receive.ThrowOnError("receive decoded frame");

        if (resend)
        {
            ffmpeg.avcodec_send_packet(_context, _packet).ThrowOnError("re-send packet to VAAPI decoder");
        }

        if (_frame->pts != ffmpeg.AV_NOPTS_VALUE)
        {
            frameRtpTimestamp = unchecked((uint)_frame->pts);
        }

        int width = _frame->width;
        int height = _frame->height;

        // Zero-copy path: export the decoded VAAPI surface as a DMA-BUF (DRM-PRIME) and surface its planes
        // directly, no CPU readback. The map holds the surface alive until the next decode unrefs it, so the
        // frame must be consumed before TryDecode is called again (single frame in flight - the pull pipeline).
        if (_gpuSurface && (AVPixelFormat)_frame->format == AVPixelFormat.AV_PIX_FMT_VAAPI
            && TryExportDmaBuf(width, height, presentationTimeNs, out frame))
        {
            return true;
        }

        // The decoded frame is a VAAPI surface; transfer it down to a CPU frame (the hardware frames context's
        // sw_format, which for HEVC 8-bit is NV12). A driver that handed back a software frame directly (no
        // hwaccel) is read out as-is.
        AVFrame* output;
        if ((AVPixelFormat)_frame->format == AVPixelFormat.AV_PIX_FMT_VAAPI)
        {
            ffmpeg.av_frame_unref(_swFrame);
            ffmpeg.av_hwframe_transfer_data(_swFrame, _frame, 0).ThrowOnError("transfer VAAPI surface to system memory");
            output = _swFrame;
        }
        else
        {
            output = _frame;
        }

        var pixelFormat = (AVPixelFormat)output->format;
        (byte[] pixels, VideoPixelFormat format) = pixelFormat == AVPixelFormat.AV_PIX_FMT_NV12
            ? (HevcDecoder.ExtractNv12(output, width, height), VideoPixelFormat.Nv12)
            : (HevcDecoder.ExtractI420(output, width, height), VideoPixelFormat.I420);

        frame = VideoFrame.FromPixels(pixels, format, width, height, presentationTimeNs);
        return true;
    }

    // Maps the current VAAPI surface (_frame) to a DRM-PRIME descriptor and flattens its objects+layers into
    // a flat DmaBufSurface (one DmaBufPlane per layer-plane, resolving each plane's fd via its object). The
    // modifier is taken from the first object (uniform across a surface in practice). Returns false (so the
    // caller falls back to CPU readback) if the driver cannot map to DRM-PRIME.
    private bool TryExportDmaBuf(int width, int height, long presentationTimeNs, out VideoFrame frame)
    {
        frame = default;

        ffmpeg.av_frame_unref(_drmFrame);
        _drmFrame->format = (int)AVPixelFormat.AV_PIX_FMT_DRM_PRIME;
        if (ffmpeg.av_hwframe_map(_drmFrame, _frame, (int)DrmPrime.HwframeMapRead) < 0 || _drmFrame->data[0] is null)
        {
            return false;
        }

        ref readonly AVDRMFrameDescriptor desc = ref *(AVDRMFrameDescriptor*)_drmFrame->data[0];
        if (desc.nb_layers <= 0 || desc.nb_objects <= 0)
        {
            return false;
        }

        string? dbg = Environment.GetEnvironmentVariable("STX_DMABUF_DEBUG");
        if (!string.IsNullOrEmpty(dbg))
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[dmabuf-export] nb_objects={desc.nb_objects} nb_layers={desc.nb_layers}");
            for (int o = 0; o < desc.nb_objects && o < DrmPrime.MaxPlanes; o++)
            {
                sb.AppendLine($"  object[{o}] fd={desc.objects[o].fd} modifier=0x{desc.objects[o].format_modifier:x16} size={desc.objects[o].size}");
            }
            for (int l = 0; l < desc.nb_layers && l < DrmPrime.MaxPlanes; l++)
            {
                ref readonly AVDRMLayerDescriptor ly = ref desc.layers[l];
                sb.AppendLine($"  layer[{l}] fourcc=0x{ly.format:x8} nb_planes={ly.nb_planes}");
                for (int p = 0; p < ly.nb_planes && p < DrmPrime.MaxPlanes; p++)
                {
                    sb.AppendLine($"    plane[{p}] object={ly.planes[p].object_index} offset={ly.planes[p].offset} pitch={ly.planes[p].pitch}");
                }
            }

            File.AppendAllText(dbg, sb.ToString());
        }

        ulong modifier = desc.objects[0].format_modifier;
        Span<DmaBufPlane> planes = stackalloc DmaBufPlane[DmaBufSurfaceMaxPlanes];
        int planeCount = 0;
        for (int l = 0; l < desc.nb_layers && planeCount < planes.Length; l++)
        {
            ref readonly AVDRMLayerDescriptor layer = ref desc.layers[l];
            for (int p = 0; p < layer.nb_planes && planeCount < planes.Length; p++)
            {
                ref readonly AVDRMPlaneDescriptor plane = ref layer.planes[p];
                int fd = desc.objects[plane.object_index].fd;
                // Carry the layer's DRM fourcc verbatim (e.g. R8 for the Y layer, GR88 for UV) so the
                // consumer rebuilds the producer's exact descriptor without guessing the driver's convention.
                planes[planeCount++] = new DmaBufPlane(fd, (uint)plane.offset, (uint)plane.pitch, layer.format);
            }
        }

        if (planeCount == 0)
        {
            return false;
        }

        // HEVC 8-bit decodes to NV12; that is the only sw_format the VAAPI frames context produces here.
        var surface = new DmaBufSurface(modifier, VideoPixelFormat.Nv12, planes[..planeCount]);
        frame = VideoFrame.FromDmaBuf(in surface, width, height, presentationTimeNs);
        return true;
    }

    // Keep in step with DmaBufSurface's inline-array capacity (4); a flat NV12 export uses 2.
    private const int DmaBufSurfaceMaxPlanes = 4;

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

        fixed (AVFrame** frame = &_frame)
        {
            ffmpeg.av_frame_free(frame);
        }

        fixed (AVFrame** swFrame = &_swFrame)
        {
            ffmpeg.av_frame_free(swFrame);
        }

        fixed (AVFrame** drmFrame = &_drmFrame)
        {
            ffmpeg.av_frame_free(drmFrame);
        }

        fixed (AVCodecContext** context = &_context)
        {
            ffmpeg.avcodec_free_context(context);
        }

        fixed (AVBufferRef** device = &_hwDevice)
        {
            ffmpeg.av_buffer_unref(device);
        }
    }
}
