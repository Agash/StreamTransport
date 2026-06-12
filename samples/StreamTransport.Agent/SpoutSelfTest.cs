#if WINDOWS_HEAD
using Agash.StreamTransport;
using Agash.StreamTransport.Codecs;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace StreamTransport.Agent;

/// <summary>
/// A standalone check (no Spout sender / OBS needed) that the GPU NV12->BGRA conversion shader compiles
/// and produces sane colours: it builds a neutral-grey NV12 texture, runs the converter, reads the centre
/// pixel back, and confirms it is grey. Run with <c>selftest</c>.
/// </summary>
internal static class SpoutSelfTest
{
    public static int Run()
    {
        nint deviceHandle = D3D11Devices.CreateForEncoder("hevc_nvenc");
        using var device = new ID3D11Device(deviceHandle);
        ID3D11DeviceContext context = device.ImmediateContext;

        const int w = 64;
        const int h = 64;
        using ID3D11Texture2D nv12 = CreateNeutralGreyNv12(device, w, h);

        using var converter = new D3D11Nv12ToBgraConverter(device);
        var nv12Frame = VideoFrame.FromSurface(nv12.NativePointer, StreamInteropKind.Spout, w, h, 0);
        nint bgraHandle = converter.Nv12ToBgra(nv12Frame, 0).Surface;

        // Copy the BGRA output into a CPU-readable staging texture and inspect the centre pixel.
        using var bgra = new ID3D11Texture2D(bgraHandle);
        bgra.AddRef();
        using ID3D11Texture2D staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = w,
            Height = h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        context.CopyResource(staging, bgra);

        MappedSubresource map = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        int b, g, r;
        unsafe
        {
            byte* center = (byte*)map.DataPointer + ((h / 2) * (int)map.RowPitch) + ((w / 2) * 4);
            b = center[0];
            g = center[1];
            r = center[2];
        }

        context.Unmap(staging, 0);
        context.Dispose();
        D3D11Devices.Release(deviceHandle);

        bool grey = InRange(r) && InRange(g) && InRange(b);
        Console.WriteLine($"CONVERTER centre pixel BGRA=({b},{g},{r}); neutral grey expected ~128.");
        Console.WriteLine(grey ? "CONVERTER-OK" : "CONVERTER-FAIL: not grey.");
        return grey ? 0 : 1;

        static bool InRange(int v) => v is >= 96 and <= 160;
    }

    /// <summary>
    /// GPU side-by-side-alpha round-trip with no Spout sender needed: synthetic BGRA (left opaque, right
    /// transparent) -> <see cref="D3D11AlphaPacker"/> (2W x H) -> hardware HEVC encode -> decode (NV12) ->
    /// <see cref="D3D11AlphaUnpacker"/> -> readback, asserting alpha survives the lossy codec. Verifies the
    /// Windows GPU alpha shaders end to end through real NVENC/AMF (the part that otherwise needed live OBS).
    /// </summary>
    public static int RunAlpha(string encoderName)
    {
        // Packed frame is 2W x H = 1280 x 720 - a size the D3D11VA HEVC decoder accepts (tiny frames fail
        // its hwaccel surface-pool init).
        const int w = 640;
        const int h = 720;
        const int packedW = w * 2;

        FFmpegLibrary.EnsureLoaded();
        if (!FFmpegLibrary.HasEncoder(encoderName))
        {
            Console.WriteLine($"ALPHA-SKIP: {encoderName} not present in this FFmpeg build.");
            return 2;
        }

        bool bgraDirect = D3D11VideoEncoder.SupportsInputFormat(encoderName, VideoPixelFormat.Bgra)
            && Environment.GetEnvironmentVariable("SELFTEST_FORCE_NV12") != "1";
        VideoPixelFormat encInput = bgraDirect ? VideoPixelFormat.Bgra : VideoPixelFormat.Nv12;

        D3D11VideoEncoder encoder;
        try
        {
            encoder = new D3D11VideoEncoder(encoderName, packedW, h, fps: 30, bitrate: 4_000_000, inputFormat: encInput);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ALPHA-SKIP: {encoderName} hardware not available: {ex.Message}");
            return 2;
        }

        var accessUnits = new List<byte[]>();
        using (encoder)
        {
            using var encDevice = new ID3D11Device(encoder.NativeDevice);
            encDevice.AddRef();
            using var packer = new D3D11AlphaPacker(encDevice);
            using D3D11BgraToNv12Converter? converter = bgraDirect ? null : new D3D11BgraToNv12Converter(encDevice);
            using ID3D11Texture2D bgraSrc = CreateAlphaBgra(encDevice, w, h);

            for (int i = 0; i < 12; i++)
            {
                nint packed = packer.PackAlpha(VideoFrame.FromSurface(bgraSrc.NativePointer, StreamInteropKind.Spout, w, h, 0), 0).Surface;
                nint encodeTex = converter is null ? packed : converter.Convert(packed, packedW, h);
                byte[]? au = encoder.EncodeTexture(encodeTex, 0);
                if (au is not null)
                {
                    accessUnits.Add(au);
                }
            }
        }

        if (accessUnits.Count == 0)
        {
            Console.WriteLine("ALPHA-FAIL: encoder produced no access units.");
            return 1;
        }

        int opaqueA, opaqueR, transparentA;
        using (var decoder = new D3D11VideoDecoder())
        {
            nint nv12 = 0;
            int dw = 0, dh = 0;
            foreach (byte[] au in accessUnits)
            {
                if (decoder.Decode(au, 0, out dw, out dh, out _) && decoder.OutputTexture != 0)
                {
                    nv12 = decoder.OutputTexture;
                }
            }

            if (nv12 == 0)
            {
                Console.WriteLine("ALPHA-FAIL: decoder produced no GPU frame.");
                return 1;
            }

            using var decDevice = new ID3D11Device(decoder.NativeDevice);
            decDevice.AddRef();
            using var unpacker = new D3D11AlphaUnpacker(decDevice);
            // dw = 2W, dh = H -> output W x H.
            nint bgraOut = unpacker.UnpackAlpha(VideoFrame.FromSurface(nv12, StreamInteropKind.Spout, dw, dh, 0), 0).Surface;
            int outW = dw / 2;
            (opaqueA, opaqueR) = SamplePixel(decDevice, bgraOut, outW, dh, outW / 4, dh / 2);       // left: opaque
            (transparentA, _) = SamplePixel(decDevice, bgraOut, outW, dh, (3 * outW) / 4, dh / 2);  // right: transparent
        }

        Console.WriteLine(
            $"ALPHA round-trip via {encoderName} (bgraDirect={bgraDirect}): opaque A={opaqueA} R={opaqueR}, transparent A={transparentA}.");
        bool ok = opaqueA > 200 && transparentA < 55 && opaqueR > 120;
        Console.WriteLine(ok ? "ALPHA-OK" : "ALPHA-FAIL: alpha did not survive the round-trip.");
        return ok ? 0 : 1;
    }

    // Synthetic BGRA W x H: uniform colour, left half fully opaque, right half fully transparent.
    private static ID3D11Texture2D CreateAlphaBgra(ID3D11Device device, int width, int height)
    {
        byte[] data = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int p = ((y * width) + x) * 4;
                data[p + 0] = 50;   // B
                data[p + 1] = 100;  // G
                data[p + 2] = 200;  // R
                data[p + 3] = (byte)(x < width / 2 ? 255 : 0); // A
            }
        }

        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
        };

        unsafe
        {
            fixed (byte* p = data)
            {
                return device.CreateTexture2D(description, [new SubresourceData((nint)p, (uint)(width * 4))]);
            }
        }
    }

    // Read one BGRA pixel back from a GPU texture; returns (alpha, red).
    private static (int Alpha, int Red) SamplePixel(ID3D11Device device, nint bgraTexture, int width, int height, int px, int py)
    {
        ID3D11DeviceContext context = device.ImmediateContext;
        using var source = new ID3D11Texture2D(bgraTexture);
        source.AddRef();
        using ID3D11Texture2D staging = device.CreateTexture2D(new Texture2DDescription
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
        context.CopyResource(staging, source);

        MappedSubresource map = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            unsafe
            {
                byte* pixel = (byte*)map.DataPointer + (py * (int)map.RowPitch) + (px * 4);
                return (pixel[3], pixel[2]);
            }
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private static ID3D11Texture2D CreateNeutralGreyNv12(ID3D11Device device, int width, int height)
    {
        // Y = 128 (mid grey), U = V = 128 (no colour).
        byte[] data = new byte[width * height * 3 / 2];
        Array.Fill(data, (byte)128);

        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
        };

        unsafe
        {
            fixed (byte* p = data)
            {
                return device.CreateTexture2D(description, [new SubresourceData((nint)p, (uint)width)]);
            }
        }
    }
}
#endif
