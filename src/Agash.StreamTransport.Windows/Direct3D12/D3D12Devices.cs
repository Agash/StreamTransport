using Agash.StreamTransport.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.System.Com;

namespace Agash.StreamTransport.Windows.Direct3D12;

/// <summary>Opens Direct3D 12 devices on a chosen GPU.</summary>
internal static unsafe class D3D12Devices
{
    /// <summary>A device on an adapter, or on the system's default GPU.</summary>
    /// <param name="adapter">The adapter, by its DXGI LUID; null for the default.</param>
    /// <returns>The ID3D12Device, owned by the caller.</returns>
    /// <exception cref="ArgumentException">The identity names no DXGI adapter.</exception>
    public static nint Create(GpuIdentity? adapter)
    {
        Guid device = typeof(ID3D12Device).GUID;
        void* created;
        if (adapter is not { } identity)
        {
            Win32.D3D12CreateDevice(
                null,
                D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
                &device,
                &created
            );
            return (nint)created;
        }

        if (identity.Kind != GpuIdentityKind.DxgiAdapterLuid)
        {
            throw new ArgumentException($"{identity} is not a DXGI adapter.", nameof(adapter));
        }

        Guid factoryId = typeof(IDXGIFactory4).GUID;
        void* factory;
        Win32.CreateDXGIFactory2(0, &factoryId, &factory);
        try
        {
            LUID luid = new()
            {
                LowPart = (uint)identity.Value,
                HighPart = (int)(identity.Value >> 32),
            };
            Guid adapterId = typeof(IDXGIAdapter1).GUID;
            void* found;
            ((IDXGIFactory4*)factory)->EnumAdapterByLuid(luid, &adapterId, &found);
            try
            {
                Win32.D3D12CreateDevice(
                    (IUnknown*)found,
                    D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
                    &device,
                    &created
                );
                return (nint)created;
            }
            finally
            {
                _ = ((IUnknown*)found)->Release();
            }
        }
        finally
        {
            _ = ((IUnknown*)factory)->Release();
        }
    }
}
