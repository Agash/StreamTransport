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

    public PooledTexture(
        D3D12TexturePool pool,
        nint texture,
        nint luma,
        nint chroma,
        nint shared = 0
    )
    {
        _pool = pool;
        Texture = texture;
        Luma = luma;
        Chroma = chroma;
        Shared = shared;
    }

    /// <summary>The texture frames are delivered in.</summary>
    public nint Texture { get; }

    /// <summary>The texture opened on the pool's Direct3D 11 bridge; zero without one.</summary>
    public nint Shared { get; }

    /// <summary>
    /// The value of the pool's release fence that the bridge's context signals once the reads recorded
    /// there before this texture came back have run; zero before its first return.
    /// </summary>
    public ulong ReleasedAt { get; set; }

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
        D3D11Bridge.Release(Shared);
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
/// <param name="bridge">
/// A Direct3D 11 device the textures are shared into, for a consumer that reads them there; null for
/// none. A texture comes back once its frame is released, but the consumer's reads on that device may
/// still be queued: the device's context signals a fence then, and the engine's queue waits for it
/// before writing the texture again.
/// </param>
internal sealed class D3D12TexturePool(
    D3D12Engine engine,
    DXGI_FORMAT format,
    int width,
    int height,
    bool shaderWritten = true,
    D3D11Bridge? bridge = null
) : IDisposable
{
    private readonly D3D12Engine _engine = engine.Hold();
    private readonly Stack<PooledTexture> _free = new();
    private readonly Lock _gate = new();
    private readonly (nint Fence, nint Opened) _released =
        bridge?.CreateFence(engine.Device) ?? default;
    private ulong _releases;
    private int _outstanding;
    private bool _disposed;

    /// <summary>The bridge the textures are shared into, or null.</summary>
    public D3D11Bridge? Bridge => bridge;

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
                if (texture.ReleasedAt != 0)
                {
                    _engine.WaitForProducer(
                        new Media.D3D12Sync(Fence: _released.Opened, Value: texture.ReleasedAt)
                    );
                }

                return texture;
            }
        }

        // Frames are delivered in COMMON. NV12 is written in place through views of its two planes where
        // the device stores to NV12; elsewhere through R8 and R8G8 planes, which stay in the UAV state, and
        // copied in. RGB is written directly.
        const D3D12_RESOURCE_STATES Common = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON;
        const D3D12_RESOURCE_STATES Writable =
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        bool shared = bridge is not null;
        return format == DXGI_FORMAT.DXGI_FORMAT_NV12 && !_engine.StoresNv12
            ? Shared(
                new PooledTexture(
                    this,
                    _engine.CreateTexture(format, width, height, false, Common, shared),
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
            )
            : Shared(
                new PooledTexture(
                    this,
                    _engine.CreateTexture(format, width, height, shaderWritten, Common, shared),
                    0,
                    0
                )
            );
    }

    // The texture with its view on the bridge, when there is one.
    private PooledTexture Shared(PooledTexture texture) =>
        bridge is null
            ? texture
            : new PooledTexture(
                this,
                texture.Texture,
                texture.Luma,
                texture.Chroma,
                bridge.OpenTexture(_engine.Device, texture.Texture)
            );

    public void Return(PooledTexture texture)
    {
        bool last;
        lock (_gate)
        {
            // Under the lock: a fence's values only go up, so returns signal in the order they count.
            if (bridge is not null)
            {
                texture.ReleasedAt = ++_releases;
                bridge.Signal(_released.Fence, texture.ReleasedAt);
            }

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
            ReleaseFences();
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
            ReleaseFences();
            _engine.Dispose();
        }
    }

    private void ReleaseFences()
    {
        D3D11Bridge.Release(_released.Fence);
        D3D12Engine.Release(_released.Opened);
    }
}
