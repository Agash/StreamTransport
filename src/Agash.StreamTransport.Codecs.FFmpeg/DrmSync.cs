using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Codecs.FFmpeg;

// Blocks on a DMA-BUF picture's DRM sync point in the kernel, for a reader with no way to wait on the GPU:
// VA-API, which takes buffers as they are. A thread waits in the driver; nothing polls.
[SupportedOSPlatform("linux")]
internal static partial class DrmSync
{
    private const uint WaitForSubmit = 1 << 1;

    private static readonly ConcurrentDictionary<ulong, int> Devices = new();

    /// <summary>Waits until the producer has signalled the picture's acquire point.</summary>
    /// <exception cref="TimeoutException">It was not signalled in time.</exception>
    public static void Wait(in DrmSyncTimeline sync, GpuIdentity device, TimeSpan timeout)
    {
        int drm = Device(device);
        Check(
            drmSyncobjFDToHandle(drm, sync.AcquireSyncobj, out uint handle),
            "drmSyncobjFDToHandle"
        );
        try
        {
            ulong point = sync.AcquirePoint;
            long deadline = MonotonicNanoseconds() + (long)(timeout.TotalMilliseconds * 1_000_000);
            int result = drmSyncobjTimelineWait(
                drm,
                ref handle,
                ref point,
                1,
                deadline,
                WaitForSubmit,
                out _
            );
            if (result == -ETIME || Marshal.GetLastPInvokeError() == ETIME)
            {
                throw new TimeoutException("The producer's GPU did not finish writing the frame.");
            }

            Check(result, "drmSyncobjTimelineWait");
        }
        finally
        {
            _ = drmSyncobjDestroy(drm, handle);
        }
    }

    // The GPU's render node, opened once for the life of the process.
    private static int Device(GpuIdentity device) =>
        Devices.GetOrAdd(
            device.Value,
            static dev =>
            {
                uint major = (uint)(((dev >> 32) & 0xfffff000) | ((dev >> 8) & 0xfff));
                uint minor = (uint)(((dev >> 12) & 0xffffff00) | (dev & 0xff));
                string path = $"/dev/char/{major}:{minor}";
                int fd = Open(path, ReadWrite | CloseOnExec);
                return fd >= 0
                    ? fd
                    : throw new InvalidOperationException(
                        $"{path} did not open (errno {Marshal.GetLastPInvokeError()})."
                    );
            }
        );

    private static long MonotonicNanoseconds() =>
        (long)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency));

    private static void Check(int result, string call)
    {
        if (result != 0)
        {
            throw new InvalidOperationException(
                $"{call} failed: {result} (errno {Marshal.GetLastPInvokeError()})."
            );
        }
    }

    private const int ETIME = 62;
    private const int ReadWrite = 2;
    private const int CloseOnExec = 0x80000;

    [LibraryImport(
        "libc",
        EntryPoint = "open",
        StringMarshalling = StringMarshalling.Utf8,
        SetLastError = true
    )]
    private static partial int Open(string path, int flags);

    [LibraryImport("libdrm.so.2", SetLastError = true)]
    private static partial int drmSyncobjFDToHandle(int fd, int objectFd, out uint handle);

    [LibraryImport("libdrm.so.2", SetLastError = true)]
    private static partial int drmSyncobjTimelineWait(
        int fd,
        ref uint handles,
        ref ulong points,
        uint count,
        long timeoutNanoseconds,
        uint flags,
        out uint firstSignaled
    );

    [LibraryImport("libdrm.so.2")]
    private static partial int drmSyncobjDestroy(int fd, uint handle);
}
