using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// Hardware HEVC decode straight into Vulkan images on Linux (the mpv model): FFmpeg decodes with a Vulkan
/// <c>hw_device_ctx</c> and <c>get_format</c> selecting <c>AV_PIX_FMT_VULKAN</c>, so each decoded frame is an
/// <c>AVVkFrame</c> whose <c>img[]</c> are ready <c>VkImage</c>s. The alpha unpack/pack compute then runs on
/// those images via the same Vulkan device (no dmabuf round-trip, no manual modifier import). Lifetime: the
/// most recently decoded frame is held until the next decode or dispose, so consume it before decoding again.
/// </summary>
/// <remarks>Compiles everywhere (FFmpeg.AutoGen); only functional on a host with a Vulkan video-decode driver.</remarks>
internal sealed unsafe class VulkanVideoDecoder : IDisposable
{
    private AVCodecContext* _context;
    private AVBufferRef* _hwDevice;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private bool _disposed;

    public VulkanVideoDecoder()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Vulkan decoding is only wired for Linux.");
        }

        AVCodec* codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_HEVC);
        if (codec is null)
        {
            throw new NotSupportedException("No HEVC decoder available in the FFmpeg build.");
        }

        _hwDevice = VulkanDevice.AcquireRef();

        _context = ffmpeg.avcodec_alloc_context3(codec);
        _context->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDevice);
        _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _context->pkt_timebase = new AVRational { num = 1, den = 90_000 };
        _context->get_format = new AVCodecContext_get_format_func
        {
            Pointer = (nint)(delegate* unmanaged[Cdecl]<AVCodecContext*, AVPixelFormat*, AVPixelFormat>)&GetVulkanFormat,
        };

        int open = ffmpeg.avcodec_open2(_context, codec, null);
        if (open < 0)
        {
            fixed (AVCodecContext** context = &_context) ffmpeg.avcodec_free_context(context);
            fixed (AVBufferRef** dev = &_hwDevice) ffmpeg.av_buffer_unref(dev);
            open.ThrowOnError("open Vulkan HEVC decoder");
        }

        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static AVPixelFormat GetVulkanFormat(AVCodecContext* context, AVPixelFormat* formats)
    {
        for (AVPixelFormat* p = formats; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if (*p == AVPixelFormat.AV_PIX_FMT_VULKAN)
            {
                return AVPixelFormat.AV_PIX_FMT_VULKAN;
            }
        }

        return *formats;
    }

    /// <summary>
    /// Decodes an access unit. On success exposes the decoded frame as a Vulkan image via <see cref="Image0"/>
    /// (the primary plane image) and reports its dimensions. Returns false when more input is needed.
    /// </summary>
    public bool TryDecode(ReadOnlySpan<byte> accessUnit, out int width, out int height)
    {
        width = 0;
        height = 0;

        ffmpeg.av_packet_unref(_packet);
        ffmpeg.av_new_packet(_packet, accessUnit.Length).ThrowOnError("allocate packet");
        accessUnit.CopyTo(new Span<byte>(_packet->data, accessUnit.Length));

        int sent = ffmpeg.avcodec_send_packet(_context, _packet);
        bool resend = sent == ffmpeg.AVERROR(ffmpeg.EAGAIN);
        if (!resend)
        {
            sent.ThrowOnError("send packet to Vulkan decoder");
        }

        int receive = ffmpeg.avcodec_receive_frame(_context, _frame);
        if (receive == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receive == ffmpeg.AVERROR_EOF)
        {
            return false;
        }

        receive.ThrowOnError("receive decoded frame");
        if (resend)
        {
            ffmpeg.avcodec_send_packet(_context, _packet).ThrowOnError("re-send packet to Vulkan decoder");
        }

        if ((AVPixelFormat)_frame->format != AVPixelFormat.AV_PIX_FMT_VULKAN || _frame->data[0] is null)
        {
            return false;
        }

        width = _frame->width;
        height = _frame->height;
        return true;
    }

    /// <summary>The <c>VkImage</c> of the most recently decoded frame's primary image (0 if none).</summary>
    public nint Image0
    {
        get
        {
            if (_frame is null || _frame->data[0] is null)
            {
                return 0;
            }

            var vkf = (AVVkFrameHead*)_frame->data[0];
            return vkf->img[0];
        }
    }

    /// <summary>
    /// Number of non-null <c>VkImage</c>s in the most recent frame. For NV12 this is 1 (a single multiplane
    /// image, planes via PLANE_0/PLANE_1 aspects) or 2 (separate Y + UV images) - which determines how the
    /// unpack compute binds the planes.
    /// </summary>
    public int ImageCount
    {
        get
        {
            if (_frame is null || _frame->data[0] is null)
            {
                return 0;
            }

            var vkf = (AVVkFrameHead*)_frame->data[0];
            int n = 0;
            while (n < 8 && vkf->img[n] != 0)
            {
                n++;
            }

            return n;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        fixed (AVPacket** packet = &_packet) ffmpeg.av_packet_free(packet);
        fixed (AVFrame** frame = &_frame) ffmpeg.av_frame_free(frame);
        fixed (AVCodecContext** context = &_context) ffmpeg.avcodec_free_context(context);
        fixed (AVBufferRef** device = &_hwDevice) ffmpeg.av_buffer_unref(device);
    }

    // Leading field of libavutil's AVVkFrame (hwcontext_vulkan.h): VkImage img[AV_NUM_DATA_POINTERS=8]. That
    // array is the first member, which is all we read here (the rest - tiling, mem[], layouts, semaphores -
    // is used by the compute path via the full ABI).
    [System.Runtime.CompilerServices.InlineArray(8)]
    private struct ImageArray
    {
        private nint _e0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AVVkFrameHead
    {
        public ImageArray img; // VkImage img[8]
    }
}
