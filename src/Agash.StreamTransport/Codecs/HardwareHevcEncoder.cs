using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// A vendor-agnostic hardware H.265 encoder that takes CPU NV12 frames and lets the hardware encoder
/// upload internally. Works with any system-memory-capable HEVC encoder - <c>hevc_nvenc</c> (NVIDIA),
/// <c>hevc_amf</c> (AMD), <c>hevc_qsv</c> (Intel), or <c>hevc_mf</c> (Media Foundation) - so the CPU
/// encode path is not tied to one GPU vendor. The constructor opens the encoder and throws
/// <see cref="HardwareEncoderUnavailableException"/> when the encoder exists in the build but no
/// supporting hardware is present. Output is an Annex-B HEVC access unit.
/// </summary>
internal sealed unsafe class HardwareHevcEncoder : IDisposable, IVideoEncoderBackend
{
    /// <summary>CPU-input encoder; no shared GPU device.</summary>
    public nint NativeDevice => 0;

    /// <inheritdoc/>
    public byte[]? Encode(in VideoFrame frame, out long capturePtsNs) =>
        EncodeNv12(frame.Pixels.Span, frame.ForceKeyframe, frame.PresentationTimeNs, out capturePtsNs);

    private readonly int _width;
    private readonly int _height;
    private AVCodecContext* _context;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private readonly Lock _rateGate = new();
    private bool _disposed;

    public HardwareHevcEncoder(string encoderName, int width, int height, int fps, long bitrate, int maxBFrames = 0)
    {
        _width = width;
        _height = height;

        AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(encoderName);
        if (codec is null)
        {
            throw new NotSupportedException($"Hardware encoder '{encoderName}' was not found in the FFmpeg build.");
        }

        _context = ffmpeg.avcodec_alloc_context3(codec);
        _context->width = width;
        _context->height = height;
        _context->time_base = new AVRational { num = 1, den = fps };
        _context->framerate = new AVRational { num = fps, den = 1 };
        _context->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
        _context->bit_rate = bitrate;
        _context->gop_size = fps * 2;
        // B-frames improve compression (valuable on a constrained uplink) at the cost of ~maxBFrames frame-times
        // of reorder latency. Default 0 for the lowest-latency monitor path; the producing-frame capture time is
        // recovered through the encoder PTS and the RTP timestamp is set explicitly, so reordered output stays
        // correctly timed.
        _context->max_b_frames = maxBFrames;

        int openResult = ffmpeg.avcodec_open2(_context, codec, null);
        if (openResult < 0)
        {
            fixed (AVCodecContext** context = &_context)
            {
                ffmpeg.avcodec_free_context(context);
            }

            throw new HardwareEncoderUnavailableException(
                $"Hardware encoder '{encoderName}' could not be opened; the required GPU is likely not present (error {openResult}).");
        }

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
    }

    /// <summary>
    /// Retune the live encoder to a new target bitrate (congestion backpressure) without reallocating the
    /// codec context, which would stall output. Sets the rate-control target/ceiling and a ~500 ms VBV buffer;
    /// FFmpeg's hardware encoders (NVENC/AMF/QSV) pick the new ceiling up within a frame or two.
    /// </summary>
    public void UpdateBitrate(long bitrateBps)
    {
        if (bitrateBps <= 0)
        {
            return;
        }

        lock (_rateGate)
        {
            if (_disposed)
            {
                return;
            }

            _context->bit_rate = bitrateBps;
            _context->rc_max_rate = bitrateBps;
            _context->rc_buffer_size = (int)Math.Min(int.MaxValue, bitrateBps / 2);
        }
    }

    /// <summary>Encode one NV12 CPU frame, returning the HEVC access unit or null if output was withheld.</summary>
    public byte[]? EncodeNv12(ReadOnlySpan<byte> nv12, bool forceKeyframe = false) =>
        EncodeNv12(nv12, forceKeyframe, 0, out _);

    /// <summary>
    /// Encode one NV12 CPU frame. <paramref name="capturePtsNs"/> is the PTS to stamp on the input frame;
    /// <paramref name="producedPtsNs"/> returns the PTS of the frame that produced the emitted access unit (they
    /// differ by the encoder pipeline depth, and reorder with B-frames). Returns the HEVC access unit or null.
    /// </summary>
    public byte[]? EncodeNv12(ReadOnlySpan<byte> nv12, bool forceKeyframe, long capturePtsNs, out long producedPtsNs)
    {
        producedPtsNs = 0;
        if (nv12.Length < _width * _height * 3 / 2)
        {
            throw new ArgumentException($"NV12 buffer too small for {_width}x{_height}.", nameof(nv12));
        }

        ffmpeg.av_frame_unref(_frame);
        _frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
        _frame->width = _width;
        _frame->height = _height;
        ffmpeg.av_frame_get_buffer(_frame, 32).ThrowOnError("allocate NV12 frame");

        fixed (byte* src = nv12)
        {
            byte* yDst = _frame->data[0];
            int yStride = _frame->linesize[0];
            for (int row = 0; row < _height; row++)
            {
                Buffer.MemoryCopy(src + (row * _width), yDst + (row * yStride), _width, _width);
            }

            byte* uvSrc = src + (_width * _height);
            byte* uvDst = _frame->data[1];
            int uvStride = _frame->linesize[1];
            for (int row = 0; row < _height / 2; row++)
            {
                Buffer.MemoryCopy(uvSrc + (row * _width), uvDst + (row * uvStride), _width, _width);
            }
        }

        // Stamp the frame's capture time as PTS; FFmpeg carries it to the output packet of whichever frame this
        // input eventually produces, so the caller can recover the producing frame's capture time even though the
        // encoder pipeline (and B-frames) make output lag and reorder relative to input.
        _frame->pts = capturePtsNs;
        // Force an IDR when requested (keyframe-on-demand): setting the picture type to I makes nvenc/amf/qsv
        // emit a key frame for this input regardless of the GOP cadence. NONE leaves the encoder's own decision.
        _frame->pict_type = forceKeyframe ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;
        ffmpeg.avcodec_send_frame(_context, _frame).ThrowOnError("send frame to encoder");

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

    public void Dispose()
    {
        lock (_rateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        fixed (AVPacket** packet = &_packet)
        {
            ffmpeg.av_packet_free(packet);
        }

        fixed (AVFrame** frame = &_frame)
        {
            ffmpeg.av_frame_free(frame);
        }

        fixed (AVCodecContext** context = &_context)
        {
            ffmpeg.avcodec_free_context(context);
        }
    }
}

/// <summary>Thrown when a hardware encoder exists in the FFmpeg build but no supporting GPU is present.</summary>
internal sealed class HardwareEncoderUnavailableException(string message) : Exception(message);
