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
/// Splits a decoded side-by-side <c>2W x H</c> NV12 texture (left = colour, right = alpha-as-luma) back into
/// a <c>W x H</c> BGRA texture with the alpha channel restored, on the GPU - no CPU readback - so the alpha
/// publish path stays zero-copy. Colour uses the same BT.709 limited->full matrix as the plain NV12->BGRA
/// converter; alpha is the right-half luma expanded from the 16..235 limited range used on pack. The inverse
/// of <see cref="D3D11AlphaPacker"/> (and byte-compatible with <c>AlphaPacking</c>'s CPU unpack).
/// </summary>
internal sealed class D3D11AlphaUnpacker : IDisposable, IAlphaUnpacker
{
    private static readonly string Hlsl = EmbeddedShader.Load("alpha_unpack.hlsl");

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

    public D3D11AlphaUnpacker(ID3D11Device device)
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
        Compiler.Compile(Hlsl, entryPoint, "alpha_unpack.hlsl", profile, out Blob blob, out Blob? errors);
        using (errors)
        {
            if (blob is null)
            {
                throw new InvalidOperationException($"Failed to compile {entryPoint}: {errors?.AsString()}");
            }
        }

        return blob;
    }

    /// <summary>Unpack a <c>2W x H</c> NV12 texture into a reused <c>W x H</c> BGRA-with-alpha texture; returns its handle.</summary>
    /// <summary>Split a decoded 2W x H colour|alpha surface frame back into a W x H BGRA surface frame (zero-copy, GPU).</summary>
    public VideoFrame UnpackAlpha(in VideoFrame packed, long presentationTimeNs) =>
        VideoFrame.FromSurface(
            UnpackCore(packed.Surface, packed.Width, packed.Height),
            StreamInteropKind.Spout, packed.Width / 2, packed.Height, presentationTimeNs)
            with { PixelFormat = VideoPixelFormat.Bgra };

    private nint UnpackCore(nint packedNv12Texture, int packedWidth, int height)
    {
        int width = packedWidth / 2;
        EnsureTarget(width, height);

        using var nv12 = new ID3D11Texture2D(packedNv12Texture);
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
