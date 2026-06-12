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
/// Packs a captured BGRA texture (which carries the source alpha) into a side-by-side <c>2W x H</c> BGRA
/// texture on the GPU - left half = opaque colour, right half = the alpha replicated to greyscale - so an
/// ordinary opaque HEVC encoder can carry transparency without any in-codec alpha support. Runs as a
/// full-screen pixel shader on the capture device, so the alpha send path stays zero-copy: no CPU readback.
/// The encoder's in-ASIC RGB->YUV maps the grey (a,a,a) right half to luma Y = 16 + 219/255*a, byte-identical
/// to <see cref="Agash.StreamTransport.Codecs.AlphaPacking"/>, so GPU- and CPU-packed frames interchange.
/// </summary>
internal sealed class D3D11AlphaPacker : IDisposable, IAlphaPacker
{
    private static readonly string Hlsl = EmbeddedShader.Load("alpha_pack.hlsl");

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11SamplerState _sampler;
    private ID3D11Texture2D? _packed;
    private ID3D11RenderTargetView? _rtv;
    private int _width;
    private int _height;
    private bool _disposed;

    public D3D11AlphaPacker(ID3D11Device device)
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
        Compiler.Compile(Hlsl, entryPoint, "alpha_pack.hlsl", profile, out Blob blob, out Blob? errors);
        using (errors)
        {
            if (blob is null)
            {
                throw new InvalidOperationException($"Failed to compile {entryPoint}: {errors?.AsString()}");
            }
        }

        return blob;
    }

    /// <summary>Pack a W x H BGRA surface frame into a 2W x H colour|alpha BGRA surface frame (zero-copy, GPU).</summary>
    public VideoFrame PackAlpha(in VideoFrame colourBgra, long presentationTimeNs) =>
        VideoFrame.FromSurface(
            PackCore(colourBgra.Surface, colourBgra.Width, colourBgra.Height),
            StreamInteropKind.Spout, colourBgra.Width * 2, colourBgra.Height, presentationTimeNs)
            with { PixelFormat = VideoPixelFormat.Bgra };

    private nint PackCore(nint bgraTexture, int width, int height)
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

        _context.OMSetRenderTargets(_rtv!);
        _context.RSSetViewport(new Viewport(0, 0, width * 2, height));
        _context.VSSetShader(_vs);
        _context.PSSetShader(_ps);
        _context.PSSetShaderResources(0, [srv]);
        _context.PSSetSampler(0, _sampler);
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.Draw(3, 0);
        _context.Flush();

        return _packed!.NativePointer;
    }

    private void EnsureTarget(int width, int height)
    {
        if (_packed is not null && _width == width && _height == height)
        {
            return;
        }

        _rtv?.Dispose();
        _packed?.Dispose();
        _packed = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)(width * 2),
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
        });
        _rtv = _device.CreateRenderTargetView(_packed);
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
        _packed?.Dispose();
        _sampler.Dispose();
        _ps.Dispose();
        _vs.Dispose();
        _context.Dispose();
    }
}
#endif
