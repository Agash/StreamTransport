using Agash.StreamTransport.Media;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Codecs.FFmpeg;

// Which GPU a backend runs on. A frame names its GPU by the platform's identity; a vendor backend
// (NVENC, AMF, QSV) can only run on its vendor's GPU; the generic APIs take the named GPU, or the
// hardware adapter with the most dedicated memory when none is named.
internal static class Adapters
{
    public static FF.GpuAdapter? Find(GpuIdentity identity) =>
        identity.Kind switch
        {
            GpuIdentityKind.DxgiAdapterLuid => FF.GpuAdapter.FindByLuid((long)identity.Value),
            GpuIdentityKind.DrmDevice => FF.GpuAdapter.FindByDrmDevice(identity.Value),
            GpuIdentityKind.MetalRegistryId => FF.GpuAdapter.Enumerate().FirstOrDefault(),
            _ => null,
        };

    public static GpuIdentity? IdentityOf(FF.GpuAdapter adapter) =>
        adapter.Luid is long luid ? GpuIdentity.FromAdapterLuid((ulong)luid)
        : adapter.DrmDeviceNumbers is [ulong first, ..] ? GpuIdentity.FromDrmDevice(first)
        : null;

    // The adapter a backend would run on for a device requirement, or null when it cannot run there.
    public static FF.GpuAdapter? For(EncoderBackend backend, GpuIdentity? device)
    {
        FF.GpuVendor? vendor = backend switch
        {
            EncoderBackend.Nvenc => FF.GpuVendor.Nvidia,
            EncoderBackend.Amf => FF.GpuVendor.Amd,
            EncoderBackend.Qsv => FF.GpuVendor.Intel,
            _ => null,
        };

        if (device is { } identity)
        {
            FF.GpuAdapter? named = Find(identity);
            return named is not null && (vendor is null || named.Vendor == vendor) ? named : null;
        }

        FF.GpuAdapter? best = null;
        foreach (FF.GpuAdapter adapter in FF.GpuAdapter.Enumerate())
        {
            if (
                !adapter.IsSoftware
                && (vendor is null || adapter.Vendor == vendor)
                && (best is null || adapter.DedicatedVideoMemory > best.DedicatedVideoMemory)
            )
            {
                best = adapter;
            }
        }

        return best;
    }
}
