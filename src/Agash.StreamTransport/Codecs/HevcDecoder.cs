using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Agash.StreamTransport;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// Decodes Annex-B HEVC access units, preferring a hardware decoder (NVDEC <c>hevc_cuvid</c>, then
/// QSV / rkmpp) and falling back to the built-in software HEVC decoder. Hardware decoders emit NV12;
/// the software decoder emits I420. The actual layout is reported back with each frame. Output planes
/// are packed tightly. (Zero-copy GPU output frames for direct publish land with the publish path.)
/// </summary>
internal sealed unsafe class HevcDecoder : IDisposable, IVideoDecoderBackend
{
    /// <summary>CPU decode: frames carry pixels, not a GPU surface.</summary>
    public StreamInteropKind OutputSurfaceKind => StreamInteropKind.None;

    /// <summary>CPU decode has no GPU device.</summary>
    public nint NativeDevice => 0;

    /// <inheritdoc/>
    public bool TryDecode(ReadOnlySpan<byte> accessUnit, uint rtpTimestamp, long presentationTimeNs, out VideoFrame frame, out uint frameRtpTimestamp)
    {
        if (!Decode(accessUnit, rtpTimestamp, out int width, out int height, out byte[] pixels, out VideoPixelFormat format, out frameRtpTimestamp))
        {
            frame = default;
            return false;
        }

        frame = VideoFrame.FromPixels(pixels, format, width, height, presentationTimeNs);
        return true;
    }

    private static readonly string[] s_hardwareDecoders = ["hevc_cuvid", "hevc_qsv", "hevc_rkmpp"];

    // Defense-in-depth: if a hardware decoder is fed this many access units yet never emits a frame, it is
    // treated as unable to handle the stream and transparently swapped for the software decoder. A false trip
    // only ever downgrades a working hardware decoder to software (a performance cost, never a correctness
    // one), so the threshold sits comfortably above any real decoder's reorder/startup latency. (The live
    // receive path's "no video" symptom was actually a signaling race - see RtcSession.Listen - not a decode
    // failure; this guard remains for genuinely incompatible senders we don't control.)
    private const int HardwareStarvationLimit = 30;

    private AVCodecContext* _context;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private bool _isHardware;
    private int _starvedPackets;
    private bool _disposed;

    /// <summary>The name of the decoder currently in use (e.g. "hevc_cuvid", or "hevc" for software).</summary>
    public string DecoderName { get; private set; } = "hevc";

    public HevcDecoder()
    {
        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();

        foreach (string name in s_hardwareDecoders)
        {
            AVCodec* hw = ffmpeg.avcodec_find_decoder_by_name(name);
            if (hw is not null && TryOpen(hw))
            {
                DecoderName = name;
                _isHardware = true;
                return;
            }
        }

        if (!TryOpenSoftware())
        {
            throw new NotSupportedException("No HEVC decoder (hardware or software) could be opened.");
        }
    }

    private bool TryOpenSoftware()
    {
        AVCodec* software = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_HEVC);
        if (software is null || !TryOpen(software))
        {
            return false;
        }

        DecoderName = "hevc";
        _isHardware = false;
        _starvedPackets = 0;
        return true;
    }

    // Replace a starved hardware decoder with the software decoder. The pending packet/frame allocations are
    // reused; the next decodable access unit (the next IDR) resumes output on the software decoder.
    private void FallBackToSoftware()
    {
        fixed (AVCodecContext** context = &_context)
        {
            ffmpeg.avcodec_free_context(context);
        }

        if (!TryOpenSoftware())
        {
            throw new NotSupportedException("The hardware HEVC decoder produced no frames and the software fallback could not be opened.");
        }
    }

    private bool TryOpen(AVCodec* codec)
    {
        AVCodecContext* context = ffmpeg.avcodec_alloc_context3(codec);

        // We stamp our own presentation time on decoded frames (the RTP clock), so the codec's packet
        // timebase is unused - but setting it to the 90 kHz RTP video clock keeps hevc_cuvid from logging
        // "Invalid pkt_timebase, passing timestamps as-is" on open.
        context->pkt_timebase = new AVRational { num = 1, den = 90_000 };

        // Low-delay decode: our stream is zero-latency-encoded (no B-frames, no reordering), so tell the
        // decoder to emit each frame as soon as it is decoded instead of holding a reorder/display buffer.
        // hevc_cuvid in particular otherwise buffers several frames, which is the bulk of the receive-side
        // A/V offset; this collapses that without affecting correctness for a non-reordered stream.
        context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

        if (ffmpeg.avcodec_open2(context, codec, null) < 0)
        {
            ffmpeg.avcodec_free_context(&context);
            return false;
        }

        _context = context;
        return true;
    }

    /// <summary>Decode one access unit. Returns true and fills the output when a frame is produced.</summary>
    public bool Decode(ReadOnlySpan<byte> accessUnit, uint rtpTimestamp, out int width, out int height, out byte[] pixels, out VideoPixelFormat format, out uint frameRtpTimestamp)
    {
        frameRtpTimestamp = rtpTimestamp;
        ffmpeg.av_packet_unref(_packet);
        ffmpeg.av_new_packet(_packet, accessUnit.Length).ThrowOnError("allocate packet");
        accessUnit.CopyTo(new Span<byte>(_packet->data, accessUnit.Length));

        // Thread the 90 kHz RTP timestamp through as the packet PTS (the context's pkt_timebase is 1/90000, so
        // the value passes through unscaled). FFmpeg propagates PTS through the decoder pipeline - including a
        // hardware decoder's reorder/surface queue - so frame->pts on output is the RTP timestamp of the access
        // unit that produced this frame, not the one just submitted. hevc_cuvid holds exactly one frame even
        // with AV_CODEC_FLAG_LOW_DELAY, so without this the decoded content would be scheduled a frame late.
        _packet->pts = rtpTimestamp;
        _packet->dts = rtpTimestamp;

        // With B-frames the decoder buffers output for reorder; send_packet then returns EAGAIN ("drain first")
        // rather than an error. Tolerate it: drain a frame to free a slot, then re-send the packet.
        int sent = ffmpeg.avcodec_send_packet(_context, _packet);
        bool resend = sent == ffmpeg.AVERROR(ffmpeg.EAGAIN);
        if (!resend)
        {
            sent.ThrowOnError("send packet to decoder");
        }

        int receive = ffmpeg.avcodec_receive_frame(_context, _frame);
        if (receive == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receive == ffmpeg.AVERROR_EOF)
        {
            if (_isHardware && ++_starvedPackets >= HardwareStarvationLimit)
            {
                FallBackToSoftware();
            }

            width = 0;
            height = 0;
            pixels = [];
            format = VideoPixelFormat.Nv12;
            return false;
        }

        receive.ThrowOnError("receive decoded frame");
        _starvedPackets = 0;

        if (resend)
        {
            ffmpeg.avcodec_send_packet(_context, _packet).ThrowOnError("re-send packet to decoder");
        }

        // The frame carries the PTS of the access unit that produced it (see above). A decoder that withholds
        // PTS (AV_NOPTS_VALUE) leaves the submitted timestamp as the best estimate.
        if (_frame->pts != ffmpeg.AV_NOPTS_VALUE)
        {
            frameRtpTimestamp = unchecked((uint)_frame->pts);
        }

        width = _frame->width;
        height = _frame->height;
        var pixelFormat = (AVPixelFormat)_frame->format;
        (pixels, format) = pixelFormat == AVPixelFormat.AV_PIX_FMT_NV12
            ? (ExtractNv12(_frame, width, height), VideoPixelFormat.Nv12)
            : (ExtractI420(_frame, width, height), VideoPixelFormat.I420);
        return true;
    }

    private static byte[] ExtractNv12(AVFrame* frame, int width, int height)
    {
        byte[] output = new byte[width * height * 3 / 2];
        int offset = 0;
        CopyPlane(frame->data[0], frame->linesize[0], width, height, output, ref offset);
        CopyPlane(frame->data[1], frame->linesize[1], width, height / 2, output, ref offset);
        return output;
    }

    private static byte[] ExtractI420(AVFrame* frame, int width, int height)
    {
        byte[] output = new byte[width * height * 3 / 2];
        int offset = 0;
        CopyPlane(frame->data[0], frame->linesize[0], width, height, output, ref offset);
        CopyPlane(frame->data[1], frame->linesize[1], width / 2, height / 2, output, ref offset);
        CopyPlane(frame->data[2], frame->linesize[2], width / 2, height / 2, output, ref offset);
        return output;
    }

    private static void CopyPlane(byte* source, int stride, int planeWidth, int planeHeight, byte[] destination, ref int offset)
    {
        for (int row = 0; row < planeHeight; row++)
        {
            Marshal.Copy((nint)(source + (row * stride)), destination, offset, planeWidth);
            offset += planeWidth;
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
