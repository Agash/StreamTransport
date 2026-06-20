using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// A zero-copy hardware H.265 encoder for macOS: it wraps a Syphon IOSurface as a CVPixelBuffer and
/// feeds it straight to <c>hevc_videotoolbox</c> (no CPU readback). The CVPixelBuffer references the
/// IOSurface in place, and its lifetime is tied to the AVFrame so VideoToolbox can hold it for the
/// duration of the encode. Output is an Annex-B HEVC access unit. macOS-only.
/// </summary>
/// <remarks>Pending verification on macOS hardware; written against the FFmpeg VideoToolbox hwframe API.</remarks>
internal sealed unsafe class VideoToolboxVideoEncoder : IDisposable, IVideoEncoderBackend
{
    /// <summary>VideoToolbox encodes the IOSurface directly; no D3D11-style shared device.</summary>
    public nint NativeDevice => 0;

    /// <inheritdoc/>
    public byte[]? Encode(in VideoFrame frame, out long capturePtsNs) =>
        EncodeIOSurface(frame.Surface, frame.PresentationTimeNs, frame.ForceKeyframe, out capturePtsNs);

    private readonly int _width;
    private readonly int _height;
    private AVCodecContext* _context;
    private AVBufferRef* _hwDevice;
    private AVBufferRef* _hwFrames;
    private AVPacket* _packet;
    private bool _disposed;

    public VideoToolboxVideoEncoder(int width, int height, int fps, long bitrate)
    {
        _width = width;
        _height = height;

        AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name("hevc_videotoolbox");
        if (codec is null)
        {
            throw new NotSupportedException("hevc_videotoolbox was not found in the FFmpeg build.");
        }

        AVBufferRef* device = null;
        ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX, null, null, 0)
            .ThrowOnError("create VideoToolbox device");
        _hwDevice = device;

        _hwFrames = ffmpeg.av_hwframe_ctx_alloc(_hwDevice);
        var frames = (AVHWFramesContext*)_hwFrames->data;
        frames->format = AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX;
        frames->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        frames->width = width;
        frames->height = height;
        ffmpeg.av_hwframe_ctx_init(_hwFrames).ThrowOnError("init VideoToolbox frames");

        _context = ffmpeg.avcodec_alloc_context3(codec);
        _context->width = width;
        _context->height = height;
        _context->time_base = new AVRational { num = 1, den = fps };
        _context->framerate = new AVRational { num = fps, den = 1 };
        _context->pix_fmt = AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX;
        _context->bit_rate = bitrate;
        _context->gop_size = fps * 2;
        _context->max_b_frames = 0;
        _context->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFrames);

        AVDictionary* options = null;
        LowLatencyEncoderOptions.Apply(&options, "hevc_videotoolbox");
        int openResult = ffmpeg.avcodec_open2(_context, codec, &options);
        ffmpeg.av_dict_free(&options);
        openResult.ThrowOnError("open hevc_videotoolbox");

        _packet = ffmpeg.av_packet_alloc();
    }

    /// <summary>Encode one Syphon IOSurface, returning the HEVC access unit or null if output was withheld.</summary>
    public byte[]? EncodeIOSurface(nint ioSurface) => EncodeIOSurface(ioSurface, 0, forceKeyframe: false, out _);

    public byte[]? EncodeIOSurface(nint ioSurface, long capturePtsNs, bool forceKeyframe, out long producedPtsNs)
    {
        producedPtsNs = 0;
        nint pixelBuffer = CoreVideoInterop.CreatePixelBufferFromIOSurface(ioSurface);
        AVFrame* frame = ffmpeg.av_frame_alloc();
        try
        {
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX;
            frame->width = _width;
            frame->height = _height;
            frame->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFrames);
            // VideoToolbox carries the CVPixelBuffer in data[3]; tie its lifetime to the frame so it is
            // released (CFRelease) when the frame is unreferenced.
            frame->data[3] = (byte*)pixelBuffer;
            frame->buf[0] = ffmpeg.av_buffer_create(
                (byte*)pixelBuffer,
                0,
                new av_buffer_create_free_func
                {
                    Pointer = (nint)(delegate* unmanaged[Cdecl]<void*, byte*, void>)&ReleasePixelBuffer,
                },
                null,
                0);
            frame->pts = capturePtsNs;
            frame->pict_type = forceKeyframe ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;

            ffmpeg.avcodec_send_frame(_context, frame).ThrowOnError("send IOSurface frame to encoder");

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
            ffmpeg.av_frame_free(&frame);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReleasePixelBuffer(void* opaque, byte* data) => CoreVideoInterop.Release((nint)data);

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
