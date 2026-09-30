using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Agash.StreamTransport.Windows.Direct3D12;

/// <summary>
/// An output texture of a processor, with the intermediate planes the shader writes it through. It is
/// counted: the processor holds it while making a frame, and each lease on the frame holds it again. It
/// goes back to its pool when the last holder lets go, and is destroyed then if the pool is gone.
/// </summary>
internal sealed class PooledTexture
{
    private readonly D3D12TexturePool _pool;
    private int _holders = 1;

    public PooledTexture(D3D12TexturePool pool, nint texture, nint luma, nint chroma)
    {
        _pool = pool;
        Texture = texture;
        Luma = luma;
        Chroma = chroma;
    }

    /// <summary>The texture frames are delivered in.</summary>
    public nint Texture { get; }

    /// <summary>The luma plane the shader writes, for NV12 output; zero otherwise.</summary>
    public nint Luma { get; }

    /// <summary>The chroma plane the shader writes, for NV12 output; zero otherwise.</summary>
    public nint Chroma { get; }

    public void Hold() => Interlocked.Increment(ref _holders);

    public void Release()
    {
        if (Interlocked.Decrement(ref _holders) == 0)
        {
            _pool.Return(this);
        }
    }

    // Taken out of the pool for a new frame.
    public void Take() => Volatile.Write(ref _holders, 1);

    public void Destroy()
    {
        D3D12Engine.Release(Texture);
        D3D12Engine.Release(Luma);
        D3D12Engine.Release(Chroma);
    }
}

/// <summary>
/// Output textures of one format and size, reused once no frame holds them. It holds its engine until it
/// is disposed and every texture it handed out has come back, since frames in its textures carry the
/// engine's fence.
/// </summary>
/// <param name="engine">The engine the textures are made on.</param>
/// <param name="format">Their format.</param>
/// <param name="width">Their width.</param>
/// <param name="height">Their height.</param>
/// <param name="shaderWritten">Whether shaders write them; copies only need them as copy targets.</param>
internal sealed class D3D12TexturePool(
    D3D12Engine engine,
    DXGI_FORMAT format,
    int width,
    int height,
    bool shaderWritten = true
) : IDisposable
{
    private readonly D3D12Engine _engine = engine.Hold();
    private readonly Stack<PooledTexture> _free = new();
    private readonly Lock _gate = new();
    private int _outstanding;
    private bool _disposed;

    public DXGI_FORMAT Format => format;

    public int Width => width;

    public int Height => height;

    /// <summary>A texture for a new frame, held once by the caller.</summary>
    public PooledTexture Rent()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _outstanding++;
            if (_free.TryPop(out PooledTexture? texture))
            {
                texture.Take();
                return texture;
            }
        }

        // Frames are delivered in COMMON. NV12 is written through R8 and R8G8 planes, which stay in the
        // UAV state, and copied in; RGB is written directly.
        const D3D12_RESOURCE_STATES Common = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON;
        const D3D12_RESOURCE_STATES Writable =
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        return format == DXGI_FORMAT.DXGI_FORMAT_NV12
            ? new PooledTexture(
                this,
                _engine.CreateTexture(format, width, height, false, Common),
                _engine.CreateTexture(
                    DXGI_FORMAT.DXGI_FORMAT_R8_UNORM,
                    width,
                    height,
                    true,
                    Writable
                ),
                _engine.CreateTexture(
                    DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM,
                    width / 2,
                    height / 2,
                    true,
                    Writable
                )
            )
            : new PooledTexture(
                this,
                _engine.CreateTexture(format, width, height, shaderWritten, Common),
                0,
                0
            );
    }

    public void Return(PooledTexture texture)
    {
        bool last;
        lock (_gate)
        {
            _outstanding--;
            if (!_disposed)
            {
                _free.Push(texture);
                return;
            }

            last = _outstanding == 0;
        }

        texture.Destroy();
        if (last)
        {
            _engine.Dispose();
        }
    }

    /// <summary>Destroys the free textures; those still held are destroyed when released.</summary>
    public void Dispose()
    {
        bool last;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            while (_free.TryPop(out PooledTexture? texture))
            {
                texture.Destroy();
            }

            last = _outstanding == 0;
        }

        if (last)
        {
            _engine.Dispose();
        }
    }
}
