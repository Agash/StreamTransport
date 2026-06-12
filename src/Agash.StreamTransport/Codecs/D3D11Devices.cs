#if WINDOWS_HEAD
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// Creates the single shared <c>ID3D11Device</c> that a GPU capture source (Spout) and the
/// <see cref="D3D11VideoEncoder"/> both use, on the adapter that backs the chosen hardware encoder.
/// Sharing one device is what makes the path zero-copy: the captured texture and the encoder's NV12 pool
/// live on the same GPU, so the copy is a GPU-to-GPU <c>CopySubresourceRegion</c> with no CPU readback.
/// The device is created with video support (encoders need it), BGRA support (Spout surfaces are BGRA),
/// and multithread protection (FFmpeg/nvenc submit on the immediate context under a lock).
/// </summary>
public static class D3D11Devices
{
    /// <summary>
    /// Create a shared device on the encoder's GPU adapter. The returned <c>ID3D11Device*</c> carries a
    /// single reference owned by the caller; release it with <see cref="Release"/> (or by adopting it into
    /// a Vortice <c>ID3D11Device</c> wrapper and disposing that). Before handing the handle to
    /// <see cref="D3D11VideoEncoder"/> for the zero-copy path, <c>AddRef</c> it once - FFmpeg's d3d11va
    /// context adopts that reference and releases it when the encoder is torn down.
    /// </summary>
    public static nint CreateForEncoder(string encoderName)
    {
        IDXGIAdapter1? adapter = null;
        int adapterIndex = GpuVendorMap.FindAdapterIndex(GpuVendorMap.ForEncoder(encoderName));
        if (adapterIndex >= 0)
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            factory.EnumAdapters1((uint)adapterIndex, out adapter);
        }

        try
        {
            D3D11.D3D11CreateDevice(
                adapter,
                adapter is null ? DriverType.Hardware : DriverType.Unknown,
                DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                out ID3D11Device? device).CheckError();

            ID3D11Device created = device ?? throw new InvalidOperationException("D3D11CreateDevice returned no device.");
            using (ID3D11Multithread multithread = created.QueryInterface<ID3D11Multithread>())
            {
                // FFmpeg and nvenc submit on the immediate context under their own lock; protect it.
                multithread.SetMultithreadProtected(true);
            }

            // Leave two references on the device (one for FFmpeg, one for the caller's Release), then drop
            // the Vortice wrapper. Created=1, +AddRef=2, +AddRef=3, Dispose releases the creation ref=2.
            created.AddRef();
            created.AddRef();
            nint handle = created.NativePointer;
            created.Dispose();
            return handle;
        }
        finally
        {
            adapter?.Dispose();
        }
    }

    /// <summary>Release the reference returned by <see cref="CreateForEncoder"/>.</summary>
    public static void Release(nint device)
    {
        if (device != 0)
        {
            // The Vortice wrapper adopts the existing reference; disposing it releases exactly once.
            using var wrapper = new ID3D11Device(device);
        }
    }
}
#endif
