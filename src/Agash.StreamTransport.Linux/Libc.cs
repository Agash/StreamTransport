using System.Runtime.InteropServices;

namespace Agash.StreamTransport.Linux;

// The C library calls the Linux devices go through: descriptors, ioctl, memory maps, poll and eventfd.
internal static unsafe partial class Libc
{
    public const int EIntr = 4;
    public const short PollIn = 1;

    // struct pollfd (poll(2)).
    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short ReturnedEvents;
    }

    [LibraryImport(
        "libc",
        EntryPoint = "open",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8
    )]
    public static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
    public static partial int Dup(int fd);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    public static partial int Ioctl(int fd, nuint request, void* argument);

    [LibraryImport("libc", EntryPoint = "mmap", SetLastError = true)]
    public static partial nint Mmap(
        nint address,
        nuint length,
        int protection,
        int flags,
        int fd,
        long offset
    );

    [LibraryImport("libc", EntryPoint = "munmap", SetLastError = true)]
    public static partial int Munmap(nint address, nuint length);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    public static partial int Poll(PollFd* fds, nuint count, int timeout);

    [LibraryImport("libc", EntryPoint = "eventfd", SetLastError = true)]
    public static partial int EventFd(uint initial, int flags);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    public static partial nint Write(int fd, void* buffer, nuint count);
}
