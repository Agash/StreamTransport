using System.Collections.Concurrent;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Codecs.FFmpeg;

// One open hardware device per device type and adapter, for the process. Opening one costs from
// milliseconds to over a second (a Vulkan instance and logical device), and encoders that cannot change
// their rate while running reopen on every rate change; sharing the device makes a reopen, a probe and a
// second session cost only the codec's own setup. FFmpeg's device contexts serialise their own use
// (lock_ctx for D3D11VA and D3D12VA, per-queue locks for Vulkan), as its command line relies on when one
// -init_hw_device feeds several encoders and filters.
internal static class SharedDevices
{
    private static readonly ConcurrentDictionary<
        (FF.HardwareDeviceType Type, string Adapter),
        Lazy<FF.HardwareDevice>
    > s_devices = new();

    // A reference to the device, opened on first use; the caller disposes it. A device that failed to
    // open is not kept, so the next caller tries again.
    public static FF.HardwareDevice Open(FF.HardwareDeviceType type, FF.GpuAdapter? adapter)
    {
        (FF.HardwareDeviceType, string) key = (type, adapter?.ToString() ?? "default");
        Lazy<FF.HardwareDevice> device = s_devices.GetOrAdd(
            key,
            static (_, state) =>
                new Lazy<FF.HardwareDevice>(
                    () =>
                        state.Adapter is null
                            ? FF.HardwareDevice.Create(state.Type)
                            : FF.HardwareDevice.CreateForAdapter(state.Type, state.Adapter),
                    LazyThreadSafetyMode.ExecutionAndPublication
                ),
            (Type: type, Adapter: adapter)
        );
        try
        {
            return device.Value.Share();
        }
        catch
        {
            _ = s_devices.TryRemove(KeyValuePair.Create(key, device));
            throw;
        }
    }
}
