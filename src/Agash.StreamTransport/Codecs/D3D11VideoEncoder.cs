#if WINDOWS_HEAD
using System.Runtime.InteropServices;
using System.Text;
using FFmpeg.AutoGen;
using D3D11Device = Vortice.Direct3D11.ID3D11Device;
using D3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using D3D11InfoQueue = Vortice.Direct3D11.Debug.ID3D11InfoQueue;
using D3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// A zero-copy hardware H.265 encoder for Windows: it owns an FFmpeg D3D11VA device and a pool of
/// D3D11 NV12 textures, and feeds <c>hevc_nvenc</c> directly from GPU textures (no CPU readback). A
/// capture source (Spout) opens its shared texture onto <see cref="NativeDevice"/> and submits it to
/// <c>EncodeTexture</c>; the frame is copied texture-to-texture on the GPU into a pool slot and
/// encoded. Output is an Annex-B HEVC access unit ready for <c>RTCPeerConnection.SendVideo</c>.
/// </summary>
internal sealed unsafe class D3D11VideoEncoder : IDisposable, IVideoEncoderBackend
{
    /// <inheritdoc/>
    public byte[]? Encode(in VideoFrame frame, out long capturePtsNs) =>
        EncodeTexture(frame.Surface, subresourceIndex: 0, frame.PresentationTimeNs, out capturePtsNs);

    private readonly int _width;
    private readonly int _height;
    private AVCodecContext* _context;
    private AVBufferRef* _hwDevice;
    private AVBufferRef* _hwFrames;
    private AVBufferRef* _qsvDevice;
    private AVBufferRef* _qsvFrames;
    private readonly bool _isQsv;
    private AVPacket* _packet;
    private nint _lockFn;
    private nint _unlockFn;
    private void* _lockCtx;
    private D3D11DeviceContext _immediateContext = null!;
    private D3D11InfoQueue? _infoQueue;
    private bool _disposed;

    private readonly AVPixelFormat _swFormat;

    // inputFormat: the surface format the capture source produces (Spout/Syphon are BGRA; the CPU camera
    //   lane is NV12). The encoder feeds it to the ASIC unchanged - nvenc/amf do the colour conversion to
    //   YUV in-hardware as part of encode, so there is no separate conversion pass.
    // externalDevice: an ID3D11Device* the encoder runs on instead of creating its own - pass the device a
    //   capture source (Spout) shares, so the captured texture and the encoder live on one GPU and the
    //   copy stays zero-copy. The encoder takes ownership of one reference (see D3D11Devices).
    public D3D11VideoEncoder(
        string encoderName,
        int width,
        int height,
        int fps,
        long bitrate,
        VideoPixelFormat inputFormat = VideoPixelFormat.Nv12,
        nint externalDevice = 0,
        bool debug = false)
    {
        _width = width;
        _height = height;
        _isQsv = encoderName.Contains("qsv", StringComparison.Ordinal);
        _swFormat = inputFormat switch
        {
            VideoPixelFormat.Nv12 => AVPixelFormat.AV_PIX_FMT_NV12,
            VideoPixelFormat.Bgra => AVPixelFormat.AV_PIX_FMT_BGRA,
            _ => throw new NotSupportedException($"Pixel format {inputFormat} is not a valid D3D11 surface format."),
        };

        AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(encoderName);
        if (codec is null)
        {
            throw new NotSupportedException($"Hardware encoder '{encoderName}' was not found in the FFmpeg build.");
        }

        _hwDevice = externalDevice != 0 ? WrapExternalDevice(externalDevice) : CreateOwnedDevice(encoderName, debug);

        var deviceContext = (AVHWDeviceContext*)_hwDevice->data;
        var d3d11Context = (AVD3D11VADeviceContext*)deviceContext->hwctx;
        NativeDevice = (nint)d3d11Context->device;
        // Use FFmpeg's own immediate context under its lock; nvenc submits on this same context, so all
        // device-context use must be serialised through the supplied lock/unlock callbacks.
        _lockFn = (nint)d3d11Context->@lock.Pointer;
        _unlockFn = (nint)d3d11Context->unlock.Pointer;
        _lockCtx = d3d11Context->lock_ctx;

        // Borrow FFmpeg's immediate context and device through typed Vortice wrappers; AddRef so our
        // Dispose balances out without releasing FFmpeg's own references.
        _immediateContext = new D3D11DeviceContext((nint)d3d11Context->device_context);
        _immediateContext.AddRef();
        if (debug)
        {
            using var d3dDevice = new D3D11Device(NativeDevice);
            d3dDevice.AddRef();
            _infoQueue = d3dDevice.QueryInterfaceOrNull<D3D11InfoQueue>();
            if (_infoQueue is not null)
            {
                // Capture messages instead of letting the debug layer DebugBreak (which crashes us here).
                _infoQueue.SetBreakOnSeverity(Vortice.Direct3D11.Debug.MessageSeverity.Corruption, false);
                _infoQueue.SetBreakOnSeverity(Vortice.Direct3D11.Debug.MessageSeverity.Error, false);
                _infoQueue.SetBreakOnSeverity(Vortice.Direct3D11.Debug.MessageSeverity.Warning, false);
            }
        }

        _hwFrames = ffmpeg.av_hwframe_ctx_alloc(_hwDevice);
        var frames = (AVHWFramesContext*)_hwFrames->data;
        frames->format = AVPixelFormat.AV_PIX_FMT_D3D11;
        frames->sw_format = _swFormat;
        frames->width = width;
        frames->height = height;
        frames->initial_pool_size = 8;
        // The pool is a texture array (one texture, pool_size slices), so the slice index in the frame's
        // data[1] is a valid CopySubresourceRegion subresource. NV12 (decoder-style) surfaces use
        // BIND_DECODER; BGRA encode-input surfaces use render-target/shader-resource binds so the encoder
        // can sample them.
        var d3d11Frames = (AVD3D11VAFramesContext*)frames->hwctx;
        d3d11Frames->BindFlags = (uint)(_swFormat == AVPixelFormat.AV_PIX_FMT_NV12
            ? D3D11_BIND_DECODER
            : D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE);
        ffmpeg.av_hwframe_ctx_init(_hwFrames).ThrowOnError("init D3D11 frames pool");

        _context = ffmpeg.avcodec_alloc_context3(codec);
        _context->width = width;
        _context->height = height;
        _context->time_base = new AVRational { num = 1, den = fps };
        _context->framerate = new AVRational { num = fps, den = 1 };
        _context->bit_rate = bitrate;
        _context->gop_size = fps * 2;
        _context->max_b_frames = 0;

        // Tell the encoder the target colour characteristics so its in-ASIC RGB->YUV conversion (BGRA
        // input) uses the right matrix and range, and the decoder reproduces the colours faithfully -
        // this is the control OBS gets from its conversion shader, without the extra pass.
        _context->colorspace = AVColorSpace.AVCOL_SPC_BT709;
        _context->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        _context->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
        _context->color_range = AVColorRange.AVCOL_RANGE_MPEG;

        if (_isQsv)
        {
            // QSV cannot consume D3D11 surfaces directly; derive a QSV device from the D3D11 device and a
            // QSV frames pool, so each captured D3D11 frame can be mapped to a QSV surface before encode.
            // (Pending verification on Intel hardware.)
            ConfigureQsvFrames(width, height);
        }
        else
        {
            _context->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
            _context->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFrames);
        }

        AVDictionary* options = null;
        LowLatencyEncoderOptions.Apply(&options, encoderName);

        int openResult = ffmpeg.avcodec_open2(_context, codec, &options);
        ffmpeg.av_dict_free(&options);
        openResult.ThrowOnError($"open encoder '{encoderName}'");

        _packet = ffmpeg.av_packet_alloc();
    }

    private static AVBufferRef* CreateOwnedDevice(string encoderName, bool debug)
    {
        // Place the D3D11 device on the adapter that backs this encoder's GPU vendor, so a multi-GPU
        // machine (e.g. NVIDIA discrete + AMD/Intel integrated) feeds the texture to the right encoder.
        int adapterIndex = GpuVendorMap.FindAdapterIndex(GpuVendorMap.ForEncoder(encoderName));
        string? deviceString = adapterIndex >= 0 ? adapterIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

        AVBufferRef* device = null;
        AVDictionary* deviceOptions = null;
        if (debug)
        {
            // Enables D3D11_CREATE_DEVICE_DEBUG so the D3D11 debug layer validates our calls.
            ffmpeg.av_dict_set(&deviceOptions, "debug", "1", 0);
        }

        int created = ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, deviceString, deviceOptions, 0);
        ffmpeg.av_dict_free(&deviceOptions);
        created.ThrowOnError("create D3D11VA device");
        return device;
    }

    private static AVBufferRef* WrapExternalDevice(nint externalDevice)
    {
        AVBufferRef* hwdevice = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (hwdevice is null)
        {
            throw new InvalidOperationException("av_hwdevice_ctx_alloc for the external D3D11 device failed.");
        }

        var deviceContext = (AVHWDeviceContext*)hwdevice->data;
        var d3d11Context = (AVD3D11VADeviceContext*)deviceContext->hwctx;
        // FFmpeg adopts this reference and releases it on uninit; the caller provisioned an extra ref for
        // exactly this (see D3D11Devices.CreateForEncoder). av_hwdevice_ctx_init creates the immediate
        // context and the submission lock.
        d3d11Context->device = (FFmpeg.AutoGen.ID3D11Device*)externalDevice;
        ffmpeg.av_hwdevice_ctx_init(hwdevice).ThrowOnError("init external D3D11 device");
        return hwdevice;
    }

    private const int D3D11_BIND_DECODER = 0x200;
    private const int D3D11_BIND_SHADER_RESOURCE = 0x8;
    private const int D3D11_BIND_RENDER_TARGET = 0x20;

    private void ConfigureQsvFrames(int width, int height)
    {
        AVBufferRef* qsvDevice = null;
        ffmpeg.av_hwdevice_ctx_create_derived(&qsvDevice, AVHWDeviceType.AV_HWDEVICE_TYPE_QSV, _hwDevice, 0)
            .ThrowOnError("derive QSV device from D3D11");
        _qsvDevice = qsvDevice;

        _qsvFrames = ffmpeg.av_hwframe_ctx_alloc(_qsvDevice);
        var qsv = (AVHWFramesContext*)_qsvFrames->data;
        qsv->format = AVPixelFormat.AV_PIX_FMT_QSV;
        qsv->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        qsv->width = width;
        qsv->height = height;
        qsv->initial_pool_size = 8;
        ffmpeg.av_hwframe_ctx_init(_qsvFrames).ThrowOnError("init QSV frames pool");

        _context->pix_fmt = AVPixelFormat.AV_PIX_FMT_QSV;
        _context->hw_frames_ctx = ffmpeg.av_buffer_ref(_qsvFrames);
    }

    private void CopyIntoPool(nint sourceTexture, int sourceIndex, nint poolTexture, int poolIndex)
    {
        using var source = new D3D11Texture2D(sourceTexture);
        using var destination = new D3D11Texture2D(poolTexture);
        source.AddRef();
        destination.AddRef();

        Lock();
        try
        {
            _immediateContext.CopySubresourceRegion(
                destination, (uint)poolIndex, 0, 0, 0, source, (uint)sourceIndex);
        }
        finally
        {
            Unlock();
        }

        ThrowOnD3D11Errors();
    }

    private void ThrowOnD3D11Errors()
    {
        if (_infoQueue is null)
        {
            return;
        }

        ulong count = _infoQueue.NumStoredMessages;
        if (count == 0)
        {
            return;
        }

        var builder = new StringBuilder();
        for (ulong i = 0; i < count; i++)
        {
            Vortice.Direct3D11.Debug.Message message = _infoQueue.GetMessage(i);
            if (message.Severity is Vortice.Direct3D11.Debug.MessageSeverity.Error
                or Vortice.Direct3D11.Debug.MessageSeverity.Corruption)
            {
                builder.Append(message.Severity).Append(": ").AppendLine(message.Description);
            }
        }

        _infoQueue.ClearStoredMessages();
        if (builder.Length > 0)
        {
            throw new InvalidOperationException("D3D11 debug layer reported errors during texture copy:\n" + builder);
        }
    }

    private AVFrame* MapToQsv(AVFrame* d3d11Frame)
    {
        AVFrame* qsvFrame = ffmpeg.av_frame_alloc();
        ffmpeg.av_hwframe_get_buffer(_qsvFrames, qsvFrame, 0).ThrowOnError("acquire QSV frame");
        ffmpeg.av_hwframe_map(qsvFrame, d3d11Frame, (int)AV_HWFRAME_MAP_DIRECT).ThrowOnError("map D3D11 frame to QSV");
        qsvFrame->pts = d3d11Frame->pts;
        return qsvFrame;
    }

    private const uint AV_HWFRAME_MAP_DIRECT = 4;

    private void Lock()
    {
        if (_lockFn == 0)
        {
            return;
        }

        var fn = (delegate* unmanaged[Cdecl]<void*, void>)_lockFn;
        fn(_lockCtx);
    }

    private void Unlock()
    {
        if (_unlockFn == 0)
        {
            return;
        }

        var fn = (delegate* unmanaged[Cdecl]<void*, void>)_unlockFn;
        fn(_lockCtx);
    }

    /// <summary>The native <c>ID3D11Device*</c> a capture source must open its shared texture onto.</summary>
    public nint NativeDevice { get; }

    /// <summary>
    /// Encode an external D3D11 NV12 texture by copying it into a pool slot on the GPU and encoding.
    /// Returns the HEVC access unit, or null if the encoder withheld output.
    /// </summary>
    public byte[]? EncodeTexture(nint texture, int subresourceIndex) =>
        EncodeTexture(texture, subresourceIndex, 0, out _);

    public byte[]? EncodeTexture(nint texture, int subresourceIndex, long capturePtsNs, out long producedPtsNs)
    {
        producedPtsNs = 0;
        AVFrame* hwFrame = ffmpeg.av_frame_alloc();
        try
        {
            ffmpeg.av_hwframe_get_buffer(_hwFrames, hwFrame, 0).ThrowOnError("acquire D3D11 pool frame");

            // Copy the source texture into the pool texture entirely on the GPU, holding FFmpeg's lock
            // so the copy does not race nvenc's own use of the shared immediate context.
            nint poolTexture = (nint)hwFrame->data[0];
            int poolIndex = (int)hwFrame->data[1];
            CopyIntoPool(texture, subresourceIndex, poolTexture, poolIndex);

            hwFrame->pts = capturePtsNs;
            AVFrame* encodeFrame = _isQsv ? MapToQsv(hwFrame) : hwFrame;
            try
            {
                ffmpeg.avcodec_send_frame(_context, encodeFrame).ThrowOnError("send frame to encoder");
            }
            finally
            {
                if (_isQsv)
                {
                    ffmpeg.av_frame_free(&encodeFrame);
                }
            }

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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> s_formatSupport = new();

    /// <summary>
    /// Probe whether <paramref name="encoderName"/> actually accepts a D3D11 surface of
    /// <paramref name="inputFormat"/> on this machine - by opening the encoder and test-encoding one frame,
    /// not by guessing from the GPU vendor. The result is cached. Use it to pick the native-format
    /// zero-copy path where the silicon supports it (e.g. BGRA straight into the encoder) and fall back to
    /// a converted format where it does not. Capability varies by GPU generation, driver, and FFmpeg build.
    /// </summary>
    public static bool SupportsInputFormat(string encoderName, VideoPixelFormat inputFormat) =>
        s_formatSupport.GetOrAdd($"{encoderName}:{inputFormat}", _ => Probe(encoderName, inputFormat));

    private static bool Probe(string encoderName, VideoPixelFormat inputFormat)
    {
        try
        {
            using var probe = new D3D11VideoEncoder(encoderName, 256, 256, 30, 1_000_000, inputFormat);
            return probe.TestEncode();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Encode one blank pool frame to confirm the encoder accepts this surface format end to end.</summary>
    private bool TestEncode()
    {
        AVFrame* hwFrame = ffmpeg.av_frame_alloc();
        try
        {
            if (ffmpeg.av_hwframe_get_buffer(_hwFrames, hwFrame, 0) < 0)
            {
                return false;
            }

            hwFrame->pts = 0;
            AVFrame* encodeFrame = _isQsv ? MapToQsv(hwFrame) : hwFrame;
            try
            {
                if (ffmpeg.avcodec_send_frame(_context, encodeFrame) < 0)
                {
                    return false;
                }
            }
            finally
            {
                if (_isQsv)
                {
                    ffmpeg.av_frame_free(&encodeFrame);
                }
            }

            ffmpeg.av_packet_unref(_packet);
            int receive = ffmpeg.avcodec_receive_packet(_context, _packet);
            return receive == 0 || receive == ffmpeg.AVERROR(ffmpeg.EAGAIN);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            ffmpeg.av_frame_free(&hwFrame);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _infoQueue?.Dispose();
        _immediateContext.Dispose();
        fixed (AVPacket** packet = &_packet)
        {
            ffmpeg.av_packet_free(packet);
        }

        fixed (AVCodecContext** context = &_context)
        {
            ffmpeg.avcodec_free_context(context);
        }

        fixed (AVBufferRef** frames = &_qsvFrames)
        {
            ffmpeg.av_buffer_unref(frames);
        }

        fixed (AVBufferRef** device = &_qsvDevice)
        {
            ffmpeg.av_buffer_unref(device);
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
#endif
