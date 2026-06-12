#if WINDOWS_HEAD
using Vortice.DXGI;

namespace Agash.StreamTransport.Codecs;

/// <summary>PCI vendor identifiers for the GPU families we target.</summary>
internal enum GpuVendor
{
    Unknown = 0,
    Nvidia = 0x10DE,
    Amd = 0x1002,
    Intel = 0x8086,
}

/// <summary>
/// Maps hardware encoder names to GPU vendors and resolves the matching DXGI adapter index, so the
/// D3D11 zero-copy path can place its device on the same GPU the chosen encoder runs on (essential on
/// multi-GPU machines, e.g. an NVIDIA discrete card next to an AMD or Intel integrated GPU).
/// </summary>
internal static class GpuVendorMap
{
    /// <summary>The GPU vendor that backs the given hardware encoder, or Unknown.</summary>
    public static GpuVendor ForEncoder(string encoderName) => encoderName switch
    {
        "hevc_nvenc" or "h264_nvenc" => GpuVendor.Nvidia,
        "hevc_amf" or "h264_amf" => GpuVendor.Amd,
        "hevc_qsv" or "h264_qsv" => GpuVendor.Intel,
        _ => GpuVendor.Unknown,
    };

    /// <summary>
    /// Find the DXGI adapter index whose vendor matches <paramref name="vendor"/>, or -1 if none. The
    /// returned index is suitable as the FFmpeg d3d11va device string.
    /// </summary>
    public static int FindAdapterIndex(GpuVendor vendor)
    {
        if (vendor == GpuVendor.Unknown)
        {
            return -1;
        }

        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint index = 0; factory.EnumAdapters1(index, out IDXGIAdapter1? adapter).Success; index++)
        {
            using (adapter)
            {
                if (adapter.Description1.VendorId == (int)vendor)
                {
                    return (int)index;
                }
            }
        }

        return -1;
    }
}
#endif
