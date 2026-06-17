using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// A zero-copy hardware HEVC decoder for macOS: it decodes through VideoToolbox into CVPixelBuffers and
/// exposes the backing IOSurface of each decoded frame, so a Syphon publisher can present it without a
/// CPU readback. The IOSurface in <see cref="OutputIOSurface"/> is valid until the next decode. macOS-only.
/// </summary>
/// <remarks>Pending verification on macOS hardware; written against the FFmpeg VideoToolbox hwaccel API.</remarks>
internal sealed unsafe class VideoToolboxVideoDecoder : IDisposable, IVideoDecoderBackend
{
    /// <summary>Decodes into a VideoToolbox IOSurface for a zero-copy Syphon publish.</summary>
    public StreamInteropKind OutputSurfaceKind => StreamInteropKind.Syphon;

    /// <summary>VideoToolbox decode exposes no D3D11-style device handle.</summary>
    public nint NativeDevice => 0;

    /// <inheritdoc/>
    public bool TryDecode(ReadOnlySpan<byte> accessUnit, uint rtpTimestamp, long presentationTimeNs, out VideoFrame frame, out uint frameRtpTimestamp)
    {
        if (!Decode(accessUnit, rtpTimestamp, out int width, out int height, out frameRtpTimestamp))
        {
            frame = default;
            return false;
        }

        frame = VideoFrame.FromSurface(OutputIOSurface, StreamInteropKind.Syphon, width, height, presentationTimeNs);
        return true;
    }

    private AVCodecContext* _context;
    private AVBufferRef* _hwDevice;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private AVFrame* _drainFrame;
    private bool _disposed;

    public VideoToolboxVideoDecoder()
    {
        AVCodec* codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_HEVC);
        if (codec is null)
        {
            throw new NotSupportedException("No HEVC decoder available in the FFmpeg build.");
        }

        AVBufferRef* device = null;
        ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX, null, null, 0)
            .ThrowOnError("create VideoToolbox device");
        _hwDevice = device;

        _context = ffmpeg.avcodec_alloc_context3(codec);
        _context->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDevice);
        // 90 kHz RTP video clock, so a packet PTS set to the RTP timestamp passes through unscaled and the
        // produced frame reports the RTP timestamp of the access unit that made it (PTS propagation).
        _context->pkt_timebase = new AVRational { num = 1, den = 90_000 };
        _context->get_format = new AVCodecContext_get_format_func
        {
            Pointer = (nint)(delegate* unmanaged[Cdecl]<AVCodecContext*, AVPixelFormat*, AVPixelFormat>)&GetVideoToolboxFormat,
        };
        // Low-delay decode: our stream is zero-latency-encoded (no B-frames/reordering). Without this the
        // VideoToolbox decoder holds frames in a reorder buffer and, since we pull one frame per access unit,
        // output falls progressively behind - the cause of the collapsed publish frame rate. The CPU
        // HevcDecoder sets the same flag for the same reason; matches FFmpeg's hw_decode.c expectations.
        _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

        ffmpeg.avcodec_open2(_context, codec, null).ThrowOnError("open VideoToolbox HEVC decoder");
        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        _drainFrame = ffmpeg.av_frame_alloc();
    }

    /// <summary>The IOSurface backing the most recently decoded frame, or 0 if none. Valid until the next decode.</summary>
    public nint OutputIOSurface { get; private set; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static AVPixelFormat GetVideoToolboxFormat(AVCodecContext* context, AVPixelFormat* formats)
    {
        for (AVPixelFormat* p = formats; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if (*p == AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX)
            {
                // The decoded CVPixelBuffer is an NV12-family VideoToolbox surface (its native output);
                // FFmpeg's VideoToolbox hwaccel will not map a BGRA decode target, so a colour-correct
                // Syphon publish needs an NV12->BGRA Metal pass (see Decode / the publish sink).
                return AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX;
            }
        }

        return *formats;
    }

    /// <summary>Decode one access unit. On success sets <see cref="OutputIOSurface"/> and returns true with the dimensions.</summary>
    public bool Decode(ReadOnlySpan<byte> accessUnit, uint rtpTimestamp, out int width, out int height, out uint frameRtpTimestamp)
    {
        width = 0;
        height = 0;
        frameRtpTimestamp = rtpTimestamp;

        ffmpeg.av_packet_unref(_packet);
        ffmpeg.av_new_packet(_packet, accessUnit.Length).ThrowOnError("allocate packet");
        accessUnit.CopyTo(new Span<byte>(_packet->data, accessUnit.Length));
        _packet->pts = rtpTimestamp;
        _packet->dts = rtpTimestamp;

        // send_packet returns EAGAIN ("drain first") when the decoder's input queue is full; tolerate it and
        // re-send after draining. Any other negative is a real send error.
        int sent = ffmpeg.avcodec_send_packet(_context, _packet);
        bool resend = sent == ffmpeg.AVERROR(ffmpeg.EAGAIN);
        if (!resend)
        {
            sent.ThrowOnError("send packet to decoder");
        }

        // Canonical hwaccel loop (FFmpeg doc/examples/hw_decode.c): drain receive_frame until EAGAIN. The
        // VideoToolbox backend can hold output internally, so a single receive leaves frames stuck and the
        // effective frame rate collapses. We keep only the freshest frame (live publish drops stale frames):
        // move_ref each received frame into _frame so the terminating EAGAIN call - which unrefs its argument -
        // cannot release the CVPixelBuffer we are about to expose.
        bool got = false;
        while (true)
        {
            int receive = ffmpeg.avcodec_receive_frame(_context, _drainFrame);
            if (receive == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receive == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            receive.ThrowOnError("receive decoded frame");
            ffmpeg.av_frame_unref(_frame);
            ffmpeg.av_frame_move_ref(_frame, _drainFrame);
            got = true;
        }

        if (resend)
        {
            ffmpeg.avcodec_send_packet(_context, _packet).ThrowOnError("re-send packet to decoder");
        }

        if (!got)
        {
            return false;
        }

        if ((AVPixelFormat)_frame->format != AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX)
        {
            throw new InvalidOperationException("Hardware decoder did not produce a VideoToolbox surface.");
        }

        if (_frame->pts != ffmpeg.AV_NOPTS_VALUE)
        {
            frameRtpTimestamp = unchecked((uint)_frame->pts);
        }

        width = _frame->width;
        height = _frame->height;
        nint pixelBuffer = (nint)_frame->data[3];
        OutputIOSurface = CoreVideoInterop.GetIOSurface(pixelBuffer);
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

        fixed (AVFrame** drainFrame = &_drainFrame)
        {
            ffmpeg.av_frame_free(drainFrame);
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
