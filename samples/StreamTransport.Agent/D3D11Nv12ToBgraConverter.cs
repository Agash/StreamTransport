#if WINDOWS_HEAD
using Agash.StreamTransport;
using Agash.StreamTransport.Codecs;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace StreamTransport.Agent;

/// <summary>
/// Converts a decoded NV12 D3D11 texture to BGRA on the GPU with a full-screen pixel shader, so the
/// publish side (Spout, which downstream OBS expects in BGRA) stays on the GPU - no CPU readback. The
/// decoder produces YUV (there is no in-ASIC RGB output on decode), so this conversion is unavoidable;
/// doing it in a shader keeps it zero-copy and lets us pin the colour matrix (BT.709), matching the
/// encode side. Output is a reusable BGRA render target.
/// </summary>
internal sealed class D3D11Nv12ToBgraConverter : IDisposable, INv12ToBgra
{
    private static readonly string Hlsl = EmbeddedShader.Load("nv12_to_bgra.hlsl");

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11SamplerState _sampler;
    private ID3D11Texture2D? _bgra;
    private ID3D11RenderTargetView? _rtv;
    private int _width;
    private int _height;
    private bool _disposed;

    public D3D11Nv12ToBgraConverter(ID3D11Device device)
    {
        _device = device;
        _context = device.ImmediateContext;

        Blob vsBlob = Compile("VSMain", "vs_5_0");
        Blob psBlob = Compile("PSMain", "ps_5_0");
        using (vsBlob)
        using (psBlob)
        {
            _vs = device.CreateVertexShader(vsBlob.AsSpan());
            _ps = device.CreatePixelShader(psBlob.AsSpan());
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

    private static Blob Compile(string entryPoint, string profile)
    {
        Compiler.Compile(Hlsl, entryPoint, "nv12_to_bgra.hlsl", profile, out Blob blob, out Blob? errors);
        using (errors)
        {
            if (blob is null)
            {
                throw new InvalidOperationException($"Failed to compile {entryPoint}: {errors?.AsString()}");
            }
        }

        return blob;
    }

    /// <summary>Convert <paramref name="nv12Texture"/> to a BGRA texture (reused across calls). Returns its handle.</summary>
    /// <summary>Convert a decoded NV12 surface frame to a BGRA surface frame for an opaque publish (zero-copy, GPU).</summary>
    public VideoFrame Nv12ToBgra(in VideoFrame nv12, long presentationTimeNs) =>
        VideoFrame.FromSurface(
            ConvertCore(nv12.Surface, nv12.Width, nv12.Height),
            StreamInteropKind.Spout, nv12.Width, nv12.Height, presentationTimeNs)
            with { PixelFormat = VideoPixelFormat.Bgra };

    private nint ConvertCore(nint nv12Texture, int width, int height)
    {
        EnsureTarget(width, height);

        using var nv12 = new ID3D11Texture2D(nv12Texture);
        nv12.AddRef();
        using ID3D11ShaderResourceView yView = _device.CreateShaderResourceView(nv12, new ShaderResourceViewDescription
        {
            Format = Format.R8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        });
        using ID3D11ShaderResourceView uvView = _device.CreateShaderResourceView(nv12, new ShaderResourceViewDescription
        {
            Format = Format.R8G8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        });

        _context.OMSetRenderTargets(_rtv!);
        _context.RSSetViewport(new Viewport(0, 0, width, height));
        _context.VSSetShader(_vs);
        _context.PSSetShader(_ps);
        _context.PSSetShaderResources(0, [yView, uvView]);
        _context.PSSetSampler(0, _sampler);
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.Draw(3, 0);
        _context.Flush();

        return _bgra!.NativePointer;
    }

    private void EnsureTarget(int width, int height)
    {
        if (_bgra is not null && _width == width && _height == height)
        {
            return;
        }

        _rtv?.Dispose();
        _bgra?.Dispose();
        _bgra = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
        });
        _rtv = _device.CreateRenderTargetView(_bgra);
        _width = width;
        _height = height;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _rtv?.Dispose();
        _bgra?.Dispose();
        _sampler.Dispose();
        _ps.Dispose();
        _vs.Dispose();
        _context.Dispose();
    }
}
#endif
