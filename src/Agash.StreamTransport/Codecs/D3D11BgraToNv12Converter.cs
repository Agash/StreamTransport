#if WINDOWS_HEAD
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// Converts a BGRA D3D11 texture to NV12 on the GPU (two full-screen passes writing the Y and UV planes of
/// an NV12 render target), so a hardware encoder that cannot ingest BGRA (e.g. an older AMF iGPU) still gets
/// its input from the GPU - no CPU readback. The BT.709 limited-range matrix matches <see cref="AlphaPacking"/>
/// and the decode-side NV12->BGRA shader, so the colour is consistent across the CPU and GPU paths. Output is
/// a reusable NV12 texture; feed it to <see cref="D3D11VideoEncoder"/> created with NV12 input.
/// </summary>
public sealed class D3D11BgraToNv12Converter : IDisposable
{
    private static readonly string Hlsl = LoadHlsl("bgra_to_nv12.hlsl");

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _psY;
    private readonly ID3D11PixelShader _psUV;
    private readonly ID3D11SamplerState _sampler;
    private ID3D11Texture2D? _nv12;
    private ID3D11RenderTargetView? _yRtv;
    private ID3D11RenderTargetView? _uvRtv;
    private int _width;
    private int _height;
    private bool _disposed;

    /// <summary>Create the converter on the supplied D3D11 device (shared for zero-copy GPU conversion).</summary>
    /// <param name="device">The D3D11 device the captured/decoded surfaces live on.</param>
    public D3D11BgraToNv12Converter(ID3D11Device device)
    {
        _device = device;
        _context = device.ImmediateContext;

        Blob vsBlob = Compile("VSMain", "vs_5_0");
        Blob psYBlob = Compile("PSY", "ps_5_0");
        Blob psUVBlob = Compile("PSUV", "ps_5_0");
        using (vsBlob)
        using (psYBlob)
        using (psUVBlob)
        {
            _vs = device.CreateVertexShader(vsBlob.AsSpan());
            _psY = device.CreatePixelShader(psYBlob.AsSpan());
            _psUV = device.CreatePixelShader(psUVBlob.AsSpan());
        }

        _sampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxLOD = float.MaxValue,
        });
    }

    private static string LoadHlsl(string logicalName)
    {
        System.Reflection.Assembly assembly = typeof(D3D11BgraToNv12Converter).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded shader '{logicalName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Blob Compile(string entryPoint, string profile)
    {
        Compiler.Compile(Hlsl, entryPoint, "bgra_to_nv12.hlsl", profile, out Blob blob, out Blob? errors);
        using (errors)
        {
            if (blob is null)
            {
                throw new InvalidOperationException($"Failed to compile {entryPoint}: {errors?.AsString()}");
            }
        }

        return blob;
    }

    /// <summary>Convert <paramref name="bgraTexture"/> (WxH) to a reused NV12 texture; returns its handle.</summary>
    public nint Convert(nint bgraTexture, int width, int height)
    {
        EnsureTarget(width, height);

        using var source = new ID3D11Texture2D(bgraTexture);
        source.AddRef();
        using ID3D11ShaderResourceView srv = _device.CreateShaderResourceView(source, new ShaderResourceViewDescription
        {
            Format = Format.B8G8R8A8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        });

        // Bind the output (Y plane) BEFORE the source SRV. If the source texture is still bound as a render
        // target by an upstream pass (e.g. D3D11AlphaPacker leaves its packed output bound), setting our RTV
        // here unbinds it first, so binding it as a shader-resource input does not trip D3D11's read/write
        // hazard - which silently nullifies the SRV and makes the shader sample black. That hazard was the
        // cause of the BGRA->NV12 converter producing an all-black (Y=16) frame on the BGRA-incapable path.
        _context.OMSetRenderTargets(_yRtv!);
        _context.VSSetShader(_vs);
        _context.PSSetShaderResources(0, [srv]);
        _context.PSSetSampler(0, _sampler);
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        // Pass 1: the full-resolution Y plane.
        _context.RSSetViewport(new Viewport(0, 0, width, height));
        _context.PSSetShader(_psY);
        _context.Draw(3, 0);

        // Pass 2: the half-resolution interleaved UV plane.
        _context.OMSetRenderTargets(_uvRtv!);
        _context.RSSetViewport(new Viewport(0, 0, width / 2, height / 2));
        _context.PSSetShader(_psUV);
        _context.Draw(3, 0);
        _context.Flush();

        return _nv12!.NativePointer;
    }

    private void EnsureTarget(int width, int height)
    {
        if (_nv12 is not null && _width == width && _height == height)
        {
            return;
        }

        _yRtv?.Dispose();
        _uvRtv?.Dispose();
        _nv12?.Dispose();
        _nv12 = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
        });
        // Per-plane render targets: R8 is the Y plane, R8G8 the half-res UV plane.
        _yRtv = _device.CreateRenderTargetView(_nv12, new RenderTargetViewDescription
        {
            Format = Format.R8_UNorm,
            ViewDimension = RenderTargetViewDimension.Texture2D,
            Texture2D = new Texture2DRenderTargetView { MipSlice = 0 },
        });
        _uvRtv = _device.CreateRenderTargetView(_nv12, new RenderTargetViewDescription
        {
            Format = Format.R8G8_UNorm,
            ViewDimension = RenderTargetViewDimension.Texture2D,
            Texture2D = new Texture2DRenderTargetView { MipSlice = 0 },
        });
        _width = width;
        _height = height;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _yRtv?.Dispose();
        _uvRtv?.Dispose();
        _nv12?.Dispose();
        _sampler.Dispose();
        _psY.Dispose();
        _psUV.Dispose();
        _vs.Dispose();
        _context.Dispose();
    }
}
#endif
