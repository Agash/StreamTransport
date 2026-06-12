#if WINDOWS_D3D11
using Agash.StreamTransport.Codecs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// Content tests for the GPU <see cref="D3D11BgraToNv12Converter"/> (the BGRA-incapable-encoder colour path).
/// Self-skips when no D3D11 hardware is present (headless CI). The key case reproduces the W-4 read/write
/// hazard - a source left bound as a render target by an upstream pass - which silently made the converter
/// sample black; the fix binds the output RTV before the source SRV.
/// </summary>
[TestClass]
public sealed class D3D11ConverterTests
{
    // Known colour B=50, G=100, R=200 -> BT.709 limited luma Y = (47*200 + 157*100 + 16*50)/256 + 16 ~= 117.
    private const byte SourceB = 50;
    private const byte SourceG = 100;
    private const byte SourceR = 200;
    private const int ExpectedY = 117;

    [TestMethod]
    public void BgraToNv12_WithSourceBoundAsRenderTarget_ProducesColourNotBlack()
    {
        ID3D11Device? device = TryCreateDevice();
        if (device is null)
        {
            Assert.Inconclusive("No D3D11 hardware device available (headless host).");
            return;
        }

        using (device)
        {
            const int w = 64;
            const int h = 64;
            using ID3D11Texture2D bgra = CreateBgra(device, w, h);

            // Reproduce the W-4 hazard: an upstream pass (D3D11AlphaPacker) leaves the source bound as the OM
            // render target. The converter must release it before sampling the source as an input.
            using ID3D11RenderTargetView sourceRtv = device.CreateRenderTargetView(bgra);
            device.ImmediateContext.OMSetRenderTargets(sourceRtv);

            using var converter = new D3D11BgraToNv12Converter(device);
            nint nv12 = converter.Convert(bgra.NativePointer, w, h);

            int y = ReadNv12Luma(device, nv12, w, h, w / 2, h / 2);
            Assert.IsTrue(
                Math.Abs(y - ExpectedY) <= 12,
                $"Converter luma Y={y} (expected ~{ExpectedY}); Y~16 means it sampled black - the read/write hazard regressed.");
        }
    }

    private static ID3D11Device? TryCreateDevice()
    {
        try
        {
            D3D11.D3D11CreateDevice(
                (IDXGIAdapter?)null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                out ID3D11Device? device).CheckError();
            return device;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ID3D11Texture2D CreateBgra(ID3D11Device device, int width, int height)
    {
        byte[] data = new byte[width * height * 4];
        for (int i = 0; i < data.Length; i += 4)
        {
            data[i + 0] = SourceB;
            data[i + 1] = SourceG;
            data[i + 2] = SourceR;
            data[i + 3] = 255;
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
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource, // mirrors D3D11AlphaPacker's output.
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

    private static int ReadNv12Luma(ID3D11Device device, nint nv12Texture, int width, int height, int px, int py)
    {
        ID3D11DeviceContext context = device.ImmediateContext;
        using var source = new ID3D11Texture2D(nv12Texture);
        source.AddRef();
        using ID3D11Texture2D staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
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
                return ((byte*)map.DataPointer)[(py * (int)map.RowPitch) + px];
            }
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }
}
#endif
