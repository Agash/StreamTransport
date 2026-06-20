#if WINDOWS_HEAD
using System.Diagnostics;
using Agash.StreamTransport;
using Agash.StreamTransport.Codecs;
using Microsoft.Extensions.Logging;
using Spout2.NET;
using Vortice.Direct3D11;
using Vortice.DXGI;
using MapMode = Vortice.Direct3D11.MapMode;

namespace StreamTransport.Agent;

/// <summary>
/// Captures a Spout sender's shared GPU texture (Windows). Spout surfaces are BGRA; whether the chosen
/// hardware encoder can ingest BGRA directly is <b>probed at runtime</b>, not assumed from the GPU vendor
/// (capability varies by silicon generation, driver, and FFmpeg build):
/// <list type="bullet">
/// <item>If the encoder accepts BGRA (e.g. nvenc, modern AMF/QSV, VideoToolbox), the texture is handed to
/// the transport as a BGRA surface frame and encoded zero-copy, the ASIC doing the colour conversion.</item>
/// <item>If it does not (e.g. an older AMF iGPU), the texture is read back and converted to NV12 on the
/// CPU and encoded through the NV12 path - correct everywhere, at the cost of the zero-copy fast path.</item>
/// </list>
/// </summary>
internal sealed class SpoutVideoCaptureSource : IVideoFrameSource, IDisposable
{
    private readonly SpoutReceiver _receiver;
    private readonly bool _bgraDirect;
    private readonly bool _alpha;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private ID3D11Texture2D? _staging;
    private IAlphaPacker? _packer;
    private D3D11BgraToNv12Converter? _converter;
    private bool _converterFailed;
    private int _stagingWidth;
    private int _stagingHeight;
    private byte[] _nv12 = [];
    private bool _disposed;

    public SpoutVideoCaptureSource(string? senderName, string encoderName, bool alpha = false, ILoggerFactory? loggerFactory = null)
    {
        _alpha = alpha;
        // One device (refcount 1) shared with the encoder on the zero-copy path; also used for Spout
        // receive and, on the fallback path, readback.
        nint deviceHandle = D3D11Devices.CreateForEncoder(encoderName);
        _bgraDirect = D3D11VideoEncoder.SupportsInputFormat(encoderName, VideoPixelFormat.Bgra);

        // Always share the device with the encoder. A BGRA-direct encoder takes the texture as-is; a
        // BGRA-incapable one gets a GPU BGRA->NV12 conversion (still no readback). AddRef once for FFmpeg
        // (it releases that reference when the encoder is torn down).
        _device = new ID3D11Device(deviceHandle);
        _device.AddRef();
        DeviceHandle = deviceHandle;

        _context = _device.ImmediateContext;
        _receiver = new SpoutReceiver(deviceHandle, senderName, loggerFactory);
    }

    /// <summary>The shared <c>ID3D11Device*</c> handed to the publisher for the zero-copy path.</summary>
    public nint DeviceHandle { get; }

    public bool TryGetFrame(out VideoFrame frame)
    {
        if (!_receiver.Receive() || !_receiver.IsFrameNew || _receiver.Texture == 0)
        {
            frame = default;
            return false;
        }

        int width = _receiver.SenderWidth;
        int height = _receiver.SenderHeight;
        if (width <= 0 || height <= 0)
        {
            frame = default;
            return false;
        }

        long timeNs = NowNs();

        // Alpha: pack colour|alpha into a 2W x H BGRA texture on the GPU first; everything downstream then
        // treats it as an ordinary (double-width) frame.
        nint texture = _receiver.Texture;
        int encodeWidth = width;
        if (_alpha)
        {
            _packer ??= new D3D11AlphaPacker(_device);
            var colour = VideoFrame.FromSurface(texture, StreamInteropKind.Spout, width, height, timeNs);
            texture = _packer.PackAlpha(colour, timeNs).Surface;
            encodeWidth = width * 2;
        }

        // BGRA-direct encoders (nvenc, modern AMF/QSV) ingest the texture as-is - the ASIC does the colour
        // conversion; nothing touches the CPU.
        if (_bgraDirect)
        {
            frame = VideoFrame.FromSurface(texture, StreamInteropKind.Spout, encodeWidth, height, timeNs)
                with { PixelFormat = VideoPixelFormat.Bgra };
            return true;
        }

        // BGRA-incapable encoder (e.g. an older AMF iGPU): prefer a GPU BGRA->NV12 conversion - still zero
        // readback. CPU readback is the last resort, only if the GPU converter is unsupported here.
        if (!_converterFailed)
        {
            try
            {
                _converter ??= new D3D11BgraToNv12Converter(_device);
                nint nv12Texture = _converter.Convert(texture, encodeWidth, height);
                frame = VideoFrame.FromSurface(nv12Texture, StreamInteropKind.Spout, encodeWidth, height, timeNs)
                    with { PixelFormat = VideoPixelFormat.Nv12 };
                return true;
            }
            catch (Exception)
            {
                _converterFailed = true; // fall back to CPU readback from here on.
            }
        }

        frame = _alpha ? ReadbackToBgra(width, height, timeNs) : ReadbackToNv12(width, height, timeNs);
        return frame.Width > 0;
    }

    private VideoFrame ReadbackToBgra(int width, int height, long timeNs)
    {
        EnsureStaging(width, height);
        using (var source = new ID3D11Texture2D(_receiver.Texture))
        {
            source.AddRef();
            _context.CopyResource(_staging!, source);
        }

        byte[] bgra = new byte[width * height * 4];
        MappedSubresource map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            unsafe
            {
                byte* src = (byte*)map.DataPointer;
                for (int y = 0; y < height; y++)
                {
                    new ReadOnlySpan<byte>(src + (y * (int)map.RowPitch), width * 4).CopyTo(bgra.AsSpan(y * width * 4));
                }
            }
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }

        return VideoFrame.FromPixels(bgra, VideoPixelFormat.Bgra, width, height, timeNs);
    }

    private VideoFrame ReadbackToNv12(int width, int height, long timeNs)
    {
        EnsureStaging(width, height);
        using (var source = new ID3D11Texture2D(_receiver.Texture))
        {
            source.AddRef();
            _context.CopyResource(_staging!, source);
        }

        MappedSubresource map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            BgraToNv12(map.DataPointer, (int)map.RowPitch, width, height, _nv12);
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }

        return VideoFrame.FromPixels(_nv12, VideoPixelFormat.Nv12, width, height, timeNs);
    }

    private void EnsureStaging(int width, int height)
    {
        if (_staging is not null && _stagingWidth == width && _stagingHeight == height)
        {
            return;
        }

        _staging?.Dispose();
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        _stagingWidth = width;
        _stagingHeight = height;
        _nv12 = new byte[width * height * 3 / 2];
    }

    private static unsafe void BgraToNv12(nint bgra, int rowPitch, int width, int height, byte[] nv12)
    {
        byte* src = (byte*)bgra;
        int uvOffset = width * height;
        for (int y = 0; y < height; y++)
        {
            byte* row = src + (y * rowPitch);
            for (int x = 0; x < width; x++)
            {
                byte b = row[(x * 4) + 0];
                byte g = row[(x * 4) + 1];
                byte r = row[(x * 4) + 2];
                nv12[(y * width) + x] = (byte)(((66 * r) + (129 * g) + (25 * b) + 128 >> 8) + 16);

                // One chroma sample per 2x2 block (BT.601, limited range).
                if ((y & 1) == 0 && (x & 1) == 0)
                {
                    int uv = uvOffset + ((y / 2) * width) + (x & ~1);
                    nv12[uv] = (byte)(((-38 * r) - (74 * g) + (112 * b) + 128 >> 8) + 128);
                    nv12[uv + 1] = (byte)(((112 * r) - (94 * g) - (18 * b) + 128 >> 8) + 128);
                }
            }
        }
    }

    private static long NowNs() => Stopwatch.GetTimestamp() * (1_000_000_000L / Stopwatch.Frequency);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        (_packer as IDisposable)?.Dispose();
        _converter?.Dispose();
        _staging?.Dispose();
        _receiver.Dispose();
        _context.Dispose();
        // Releases the source's reference. On the zero-copy path FFmpeg releases its own (AddRef'd) one
        // when the encoder is disposed.
        _device.Dispose();
    }
}
#endif
