#if WINDOWS_HEAD
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using D3D11Device = Vortice.Direct3D11.ID3D11Device;
using D3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using D3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// A zero-copy hardware HEVC decoder for Windows: it decodes directly into D3D11 NV12 textures via the
/// D3D11VA hardware path and hands each decoded frame out as a GPU surface (no CPU readback), so a
/// publisher (Spout) can present it zero-copy. Each decoded frame is copied on the GPU from the
/// decoder's transient pool slot into a stable output texture (Vortice), which is exposed through
/// <see cref="OutputTexture"/> and remains valid until the next decode.
/// </summary>
internal sealed unsafe class D3D11VideoDecoder : IDisposable, IVideoDecoderBackend
{
    /// <summary>Decodes into a D3D11 NV12 texture for a zero-copy Spout publish.</summary>
    public StreamInteropKind OutputSurfaceKind => StreamInteropKind.Spout;

    /// <inheritdoc/>
    public bool TryDecode(ReadOnlySpan<byte> accessUnit, uint rtpTimestamp, long presentationTimeNs, out VideoFrame frame, out uint frameRtpTimestamp)
    {
        if (!Decode(accessUnit, rtpTimestamp, out int width, out int height, out frameRtpTimestamp))
        {
            frame = default;
            return false;
        }

        frame = VideoFrame.FromSurface(OutputTexture, StreamInteropKind.Spout, width, height, presentationTimeNs);
        return true;
    }

    private AVCodecContext* _context;
    private AVBufferRef* _hwDevice;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private D3D11DeviceContext _immediateContext = null!;
    private D3D11Texture2D? _outputTexture;
    private int _outputWidth;
    private int _outputHeight;
    private bool _disposed;

    public D3D11VideoDecoder()
    {
        AVCodec* codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_HEVC);
        if (codec is null)
        {
            throw new NotSupportedException("No HEVC decoder available in the FFmpeg build.");
        }

        AVBufferRef* device = null;
        ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0)
            .ThrowOnError("create D3D11VA device");
        _hwDevice = device;

        var deviceContext = (AVHWDeviceContext*)_hwDevice->data;
        var d3d11Context = (AVD3D11VADeviceContext*)deviceContext->hwctx;
        NativeDevice = (nint)d3d11Context->device;
        _immediateContext = new D3D11DeviceContext((nint)d3d11Context->device_context);
        _immediateContext.AddRef();

        _context = ffmpeg.avcodec_alloc_context3(codec);
        _context->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDevice);
        // Low-delay decode: the stream is zero-latency-encoded (no reordering), so emit each frame as soon
        // as it is decoded rather than holding a display-reorder buffer (cuts the receive-side A/V offset).
        _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        // 90 kHz RTP video clock, so a packet PTS set to the RTP timestamp passes through unscaled and the
        // produced frame reports the RTP timestamp of the access unit that made it (PTS propagation).
        _context->pkt_timebase = new AVRational { num = 1, den = 90_000 };
        _context->get_format = new AVCodecContext_get_format_func
        {
            Pointer = (nint)(delegate* unmanaged[Cdecl]<AVCodecContext*, AVPixelFormat*, AVPixelFormat>)&GetD3D11Format,
        };

        ffmpeg.avcodec_open2(_context, codec, null).ThrowOnError("open D3D11 HEVC decoder");
        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
    }

    /// <summary>The native <c>ID3D11Device*</c> the output texture lives on; a publisher opens its shared texture here.</summary>
    public nint NativeDevice { get; }

    /// <summary>The native <c>ID3D11Texture2D*</c> holding the most recently decoded NV12 frame, or 0 if none yet.</summary>
    public nint OutputTexture => _outputTexture?.NativePointer ?? 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static AVPixelFormat GetD3D11Format(AVCodecContext* context, AVPixelFormat* formats)
    {
        for (AVPixelFormat* p = formats; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if (*p == AVPixelFormat.AV_PIX_FMT_D3D11)
            {
                return AVPixelFormat.AV_PIX_FMT_D3D11;
            }
        }

        return *formats;
    }

    /// <summary>
    /// Decode one access unit. When a frame is produced, copies it into the stable output texture and
    /// returns true with the dimensions; <see cref="OutputTexture"/> then holds the NV12 GPU frame.
    /// </summary>
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
            return false;
        }

        receive.ThrowOnError("receive decoded frame");

        if (resend)
        {
            ffmpeg.avcodec_send_packet(_context, _packet).ThrowOnError("re-send packet to decoder");
        }

        if ((AVPixelFormat)_frame->format != AVPixelFormat.AV_PIX_FMT_D3D11)
        {
            throw new InvalidOperationException("Hardware decoder did not produce a D3D11 surface.");
        }

        if (_frame->pts != ffmpeg.AV_NOPTS_VALUE)
        {
            frameRtpTimestamp = unchecked((uint)_frame->pts);
        }

        width = _frame->width;
        height = _frame->height;
        EnsureOutputTexture(width, height);

        nint poolTexture = (nint)_frame->data[0];
        int poolIndex = (int)_frame->data[1];
        using var source = new D3D11Texture2D(poolTexture);
        source.AddRef();
        _immediateContext.CopySubresourceRegion(_outputTexture!, 0, 0, 0, 0, source, (uint)poolIndex);
        return true;
    }

    private void EnsureOutputTexture(int width, int height)
    {
        if (_outputTexture is not null && _outputWidth == width && _outputHeight == height)
        {
            return;
        }

        _outputTexture?.Dispose();
        using var device = new D3D11Device(NativeDevice);
        device.AddRef();

        var description = new Vortice.Direct3D11.Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.NV12,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = Vortice.Direct3D11.ResourceUsage.Default,
            BindFlags = Vortice.Direct3D11.BindFlags.ShaderResource,
            CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.None,
        };

        _outputTexture = device.CreateTexture2D(description);
        _outputWidth = width;
        _outputHeight = height;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _outputTexture?.Dispose();
        _immediateContext.Dispose();
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

        fixed (AVBufferRef** device = &_hwDevice)
        {
            ffmpeg.av_buffer_unref(device);
        }
    }
}
#endif
