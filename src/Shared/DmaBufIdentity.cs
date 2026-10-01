using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Agash.StreamTransport.Media;

/// <summary>
/// Which DMA-BUF a file descriptor refers to. Descriptors are handles: a producer that hands each plane
/// its own descriptor (PipeWire does) may give two numbers for one buffer, so buffers are told apart by
/// what the descriptors point at, the device and inode of the DMA-BUF.
/// </summary>
[SupportedOSPlatform("linux")]
internal static unsafe partial class DmaBufIdentity
{
    /// <summary>The DMA-BUF's device and inode.</summary>
    /// <param name="fd">A descriptor of the DMA-BUF.</param>
    /// <returns>Its identity, the same for every descriptor of one buffer.</returns>
    /// <exception cref="IOException">The descriptor could not be examined.</exception>
    public static (ulong Device, ulong Inode) Of(int fd)
    {
        // st_dev and st_ino open struct stat on every Linux architecture.
        ulong* stat = stackalloc ulong[32];
        if (Fstat(fd, stat) != 0)
        {
            throw new IOException(
                $"The DMA-BUF could not be identified (errno {Marshal.GetLastPInvokeError()})."
            );
        }

        return (stat[0], stat[1]);
    }

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int Fstat(int fd, ulong* stat);
}
