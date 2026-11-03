using System.Runtime.InteropServices;
using CoreFoundation;
using ObjCRuntime;

namespace Agash.StreamTransport.MacOS.Audio;

/// <summary>
/// The Core Audio hardware's output devices, read through the HAL's property API: each by its UID, which
/// stays the same across reboots, where the numeric device ID does not.
/// </summary>
internal static unsafe partial class CoreAudioDevices
{
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";

    private const uint SystemObject = 1;
    private const uint ScopeGlobal = 0x676C6F62; // 'glob'
    private const uint ScopeOutput = 0x6F757470; // 'outp'
    private const uint ElementMain = 0;
    private const uint Devices = 0x64657623; // 'dev#'
    private const uint DefaultOutputDevice = 0x644F7574; // 'dOut'
    private const uint DeviceUid = 0x75696420; // 'uid '
    private const uint DeviceName = 0x6C6E616D; // 'lnam'
    private const uint Streams = 0x73746D23; // 'stm#'
    private const uint TranslateUidToDevice = 0x75696464; // 'uidd'

    /// <summary>The devices that play audio, the default one flagged.</summary>
    /// <returns>Each device's UID and name.</returns>
    public static List<(string Uid, string Name, bool IsDefault)> ListOutputs()
    {
        List<(string, string, bool)> outputs = [];
        _ = TryGetUInt32(SystemObject, DefaultOutputDevice, ScopeGlobal, out uint defaultDevice);

        PropertyAddress address = new(Devices, ScopeGlobal, ElementMain);
        uint size;
        if (
            AudioObjectGetPropertyDataSize(SystemObject, &address, 0, null, &size) != 0
            || size == 0
        )
        {
            return outputs;
        }

        uint[] ids = new uint[size / sizeof(uint)];
        fixed (uint* buffer = ids)
        {
            if (AudioObjectGetPropertyData(SystemObject, &address, 0, null, &size, buffer) != 0)
            {
                return outputs;
            }
        }

        foreach (uint id in ids)
        {
            if (!HasOutputStreams(id) || GetString(id, DeviceUid) is not { } uid)
            {
                continue;
            }

            string? name = GetString(id, DeviceName);
            outputs.Add((uid, string.IsNullOrWhiteSpace(name) ? uid : name, id == defaultDevice));
        }

        return outputs;
    }

    /// <summary>The device a UID names now, or null when it is not connected.</summary>
    /// <param name="uid">The device's UID.</param>
    /// <returns>Its device ID.</returns>
    public static uint? DeviceFor(string uid)
    {
        using CFString text = new(uid);
        IntPtr handle = text.Handle;
        uint device = 0;
        uint size = sizeof(uint);
        PropertyAddress address = new(TranslateUidToDevice, ScopeGlobal, ElementMain);
        int status = AudioObjectGetPropertyData(
            SystemObject,
            &address,
            (uint)sizeof(IntPtr),
            &handle,
            &size,
            &device
        );
        return status == 0 && device != 0 ? device : null;
    }

    private static bool HasOutputStreams(uint device)
    {
        PropertyAddress address = new(Streams, ScopeOutput, ElementMain);
        uint size;
        return AudioObjectGetPropertyDataSize(device, &address, 0, null, &size) == 0 && size > 0;
    }

    private static bool TryGetUInt32(uint target, uint selector, uint scope, out uint value)
    {
        PropertyAddress address = new(selector, scope, ElementMain);
        uint result = 0;
        uint size = sizeof(uint);
        int status = AudioObjectGetPropertyData(target, &address, 0, null, &size, &result);
        value = result;
        return status == 0;
    }

    private static string? GetString(uint device, uint selector)
    {
        PropertyAddress address = new(selector, ScopeGlobal, ElementMain);
        IntPtr text = IntPtr.Zero;
        uint size = (uint)sizeof(IntPtr);
        if (
            AudioObjectGetPropertyData(device, &address, 0, null, &size, &text) != 0
            || text == IntPtr.Zero
        )
        {
            return null;
        }

        // The HAL hands over a retained CFString; this takes ownership of it.
        using CFString owned = Runtime.GetINativeObject<CFString>(text, owns: true)!;
        return owned.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyAddress(uint selector, uint scope, uint element)
    {
        public readonly uint Selector = selector;
        public readonly uint Scope = scope;
        public readonly uint Element = element;
    }

    [LibraryImport(CoreAudio)]
    private static partial int AudioObjectGetPropertyDataSize(
        uint objectId,
        PropertyAddress* address,
        uint qualifierSize,
        void* qualifier,
        uint* dataSize
    );

    [LibraryImport(CoreAudio)]
    private static partial int AudioObjectGetPropertyData(
        uint objectId,
        PropertyAddress* address,
        uint qualifierSize,
        void* qualifier,
        uint* dataSize,
        void* data
    );
}
