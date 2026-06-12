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
    public byte[]? Encode(in VideoFrame frame, out long capturePtsNs) =>
        EncodeNv12(frame.Pixels.ToArray(), frame.Width, frame.Height, frame.PresentationTimeNs, out capturePtsNs);

    private AVCodecContext* _context;
    private AVBufferRef* _hwDevice;
    private AVBufferRef* _hwFrames;
    private AVPacket* _packet;
    private AVFrame* _swFrame;
    private bool _disposed;

    public VaapiVideoEncoder(int width, int height, int fps, long bitrate, string? renderNode = null)
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

        AVBufferRef* device = null;
        // Default to the first render node; a multi-GPU box can pass e.g. /dev/dri/renderD129.
        ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, renderNode, null, 0)
            .ThrowOnError("create VAAPI device");
        _hwDevice = device;

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

        AVDictionary* options = null;
        LowLatencyEncoderOptions.Apply(&options, "hevc_vaapi");
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
        EncodeNv12(nv12, width, height, 0, out _);

    public byte[]? EncodeNv12(byte[] nv12, int width, int height, long capturePtsNs, out long producedPtsNs)
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

    // TODO(linux-dmabuf-zerocopy): import a PipeWire DMA-BUF fd straight into a VAAPI surface
    // (AV_PIX_FMT_DRM_PRIME -> av_hwframe_map, AV_HWFRAME_MAP_DIRECT) for a fully zero-copy encode.
    // FFmpeg.AutoGen does not expose the AVDRM*Descriptor structs, so they must be hand-defined to the
    // exact FFmpeg ABI and verified against a real VAAPI driver - a Linux-machine task. The CPU NV12
    // upload path (EncodeNv12) is the working baseline until then.

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
