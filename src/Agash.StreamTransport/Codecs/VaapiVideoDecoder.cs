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
    /// <summary>VAAPI surfaces are transferred down to CPU NV12; no GPU surface is surfaced.</summary>
    public StreamInteropKind OutputSurfaceKind => StreamInteropKind.None;

    /// <summary>CPU-output decode (the VAAPI surface is read back); no shared GPU device.</summary>
    public nint NativeDevice => 0;

    private AVCodecContext* _context;
    private AVBufferRef* _hwDevice;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private AVFrame* _swFrame;
    private bool _disposed;

    public VaapiVideoDecoder(string? renderNode = null)
    {
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
