using System.Runtime.InteropServices;

namespace Agash.StreamTransport.Threading;

/// <summary>Scheduling for the few threads whose lateness is heard or seen.</summary>
internal static partial class MediaThreads
{
    private const int PriorityProcess = 0;

    // Above everything at the default nice value, and below PipeWire's and the kernel's realtime threads.
    private const int RaisedNice = -10;

    /// <summary>
    /// Raises the calling thread above ordinary work, such as a game on every core. On Linux
    /// <see cref="Thread.Priority"/> does nothing, so the thread's nice value is lowered, which needs
    /// CAP_SYS_NICE or an RLIMIT_NICE (<c>LimitNICE=</c> in a systemd unit); without either it keeps the
    /// default, as the outcome is only a less punctual thread.
    /// </summary>
    public static void Raise()
    {
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        if (OperatingSystem.IsLinux())
        {
            // With no target, setpriority takes the calling thread on Linux, not its process.
            _ = setpriority(PriorityProcess, 0, RaisedNice);
        }
    }

    [LibraryImport("libc", EntryPoint = "setpriority")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int setpriority(int which, uint who, int priority);
}
