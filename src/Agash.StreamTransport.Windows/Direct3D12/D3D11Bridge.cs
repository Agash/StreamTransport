using System.Collections.Concurrent;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.System.Com;

namespace Agash.StreamTransport.Windows.Direct3D12;

/// <summary>
/// A native Direct3D 11 device on a Direct3D 12 device's adapter, which Direct3D 12 resources and fences
/// are shared into: how frames made on Direct3D 12 reach an encoder that runs only on a native Direct3D 11
/// device. NVENC refuses an 11-on-12 device ("no encode device") and AMF takes Direct3D 11 surfaces, so
/// the textures are shared with handles and ordered with a shared fence, as OBS feeds NVENC; the GPU
/// never waits on the CPU.
/// </summary>
/// <remarks>
/// The device is multithread protected: its immediate context is used by the encoder's copies and by the
/// producer's release signals, from their own threads, and Direct3D 11 orders the calls as they are made.
/// One bridge serves each adapter for the process.
/// </remarks>
internal sealed unsafe class D3D11Bridge
{
    private const uint GenericAll = 0x10000000;

    private static readonly ConcurrentDictionary<ulong, Lazy<D3D11Bridge>> s_bridges = new();

    private readonly ID3D11Device5* _device;
    private readonly ID3D11DeviceContext4* _context;

    private D3D11Bridge(Media.GpuIdentity adapter)
    {
        Adapter = adapter;
        Guid factoryId = typeof(IDXGIFactory4).GUID;
        void* factory;
        Win32.CreateDXGIFactory2(0, &factoryId, &factory);
        IDXGIAdapter* found;
        try
        {
            LUID luid = new()
            {
                LowPart = (uint)adapter.Value,
                HighPart = (int)(adapter.Value >> 32),
            };
            Guid adapterId = typeof(IDXGIAdapter).GUID;
            void* enumerated;
            ((IDXGIFactory4*)factory)->EnumAdapterByLuid(luid, &adapterId, &enumerated);
            found = (IDXGIAdapter*)enumerated;
        }
        finally
        {
            _ = ((IUnknown*)factory)->Release();
        }

        ID3D11Device* device;
        ID3D11DeviceContext* context;
        try
        {
            // Video support: FFmpeg's Direct3D 11 device needs the video device and context.
            D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1;
            Win32.D3D11CreateDevice(
                found,
                D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN,
                HMODULE.Null,
                D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT
                    | D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                &level,
                1,
                Win32.D3D11_SDK_VERSION,
                &device,
                null,
                &context
            );
        }
        finally
        {
            _ = ((IUnknown*)found)->Release();
        }

        try
        {
            _device = (ID3D11Device5*)QueryInterface(device, typeof(ID3D11Device5).GUID);
            _context = (ID3D11DeviceContext4*)QueryInterface(
                context,
                typeof(ID3D11DeviceContext4).GUID
            );
            var multithread = (ID3D11Multithread*)QueryInterface(
                context,
                typeof(ID3D11Multithread).GUID
            );
            _ = multithread->SetMultithreadProtected(true);
            _ = multithread->Release();
        }
        finally
        {
            _ = device->Release();
            _ = context->Release();
        }
    }

    /// <summary>The adapter the device runs on.</summary>
    public Media.GpuIdentity Adapter { get; }

    /// <summary>The ID3D11Device, borrowed.</summary>
    public nint Device => (nint)_device;

    /// <summary>The bridge to a Direct3D 12 device's adapter, opened on first use.</summary>
    /// <param name="adapter">The adapter, by its DXGI LUID.</param>
    /// <returns>The bridge, which lives as long as the process.</returns>
    public static D3D11Bridge For(Media.GpuIdentity adapter) =>
        s_bridges
            .GetOrAdd(
                adapter.Value,
                static (_, identity) =>
                    new Lazy<D3D11Bridge>(
                        () => new D3D11Bridge(identity),
                        LazyThreadSafetyMode.ExecutionAndPublication
                    ),
                adapter
            )
            .Value;

    /// <summary>Opens a Direct3D 12 texture made with <c>D3D12_HEAP_FLAG_SHARED</c> on this device.</summary>
    /// <param name="device">The ID3D12Device it was made on.</param>
    /// <param name="resource">The ID3D12Resource.</param>
    /// <returns>The ID3D11Texture2D, owned by the caller.</returns>
    public nint OpenTexture(nint device, nint resource)
    {
        HANDLE handle = Share(device, resource);
        try
        {
            Guid iid = typeof(ID3D11Texture2D).GUID;
            void* texture;
            _device->OpenSharedResource1(handle, &iid, &texture);
            return (nint)texture;
        }
        finally
        {
            _ = Win32.CloseHandle(handle);
        }
    }

    /// <summary>Opens a Direct3D 12 fence made with <c>D3D12_FENCE_FLAG_SHARED</c> on this device.</summary>
    /// <param name="device">The ID3D12Device it was made on.</param>
    /// <param name="fence">The ID3D12Fence.</param>
    /// <returns>The ID3D11Fence, owned by the caller.</returns>
    public nint OpenFence(nint device, nint fence)
    {
        HANDLE handle = Share(device, fence);
        try
        {
            Guid iid = typeof(ID3D11Fence).GUID;
            void* opened;
            _device->OpenSharedFence(handle, &iid, &opened);
            return (nint)opened;
        }
        finally
        {
            _ = Win32.CloseHandle(handle);
        }
    }

    /// <summary>
    /// A fence of this device, shared, and the same fence opened on a Direct3D 12 device: this device's
    /// context signals it and the Direct3D 12 queue waits for it.
    /// </summary>
    /// <param name="device">The ID3D12Device to open it on.</param>
    /// <returns>The ID3D11Fence and the ID3D12Fence, both owned by the caller.</returns>
    public (nint Fence, nint Opened) CreateFence(nint device)
    {
        Guid iid = typeof(ID3D11Fence).GUID;
        void* created;
        _device->CreateFence(0, D3D11_FENCE_FLAG.D3D11_FENCE_FLAG_SHARED, &iid, &created);
        var fence = (ID3D11Fence*)created;
        HANDLE handle;
        fence->CreateSharedHandle(null, GenericAll, default, &handle);
        try
        {
            Guid opened = typeof(ID3D12Fence).GUID;
            void* shared;
            ((ID3D12Device*)device)->OpenSharedHandle(handle, &opened, &shared);
            return ((nint)fence, (nint)shared);
        }
        catch
        {
            _ = fence->Release();
            throw;
        }
        finally
        {
            _ = Win32.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Signals a fence of this device on its context, after every read already recorded there, and
    /// submits the context so a Direct3D 12 queue waiting on the fence is not left waiting for the next
    /// submission.
    /// </summary>
    /// <param name="fence">The ID3D11Fence.</param>
    /// <param name="value">The value.</param>
    public void Signal(nint fence, ulong value)
    {
        _context->Signal((ID3D11Fence*)fence, value);
        _context->Flush();
    }

    /// <summary>Releases a COM object.</summary>
    /// <param name="unknown">The object; zero for none.</param>
    public static void Release(nint unknown)
    {
        if (unknown != 0)
        {
            _ = ((IUnknown*)unknown)->Release();
        }
    }

    private static HANDLE Share(nint device, nint child)
    {
        HANDLE handle;
        ((ID3D12Device*)device)->CreateSharedHandle(
            (ID3D12DeviceChild*)child,
            null,
            GenericAll,
            default,
            &handle
        );
        return handle;
    }

    private static void* QueryInterface(void* unknown, Guid iid)
    {
        void* result;
        ((IUnknown*)unknown)->QueryInterface(&iid, &result).ThrowOnFailure();
        return result;
    }
}
