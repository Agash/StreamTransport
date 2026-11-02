using System.Runtime.InteropServices;

namespace Agash.StreamTransport.Threading;

/// <summary>Scheduling for the few threads whose lateness is heard or seen.</summary>
internal static partial class MediaThreads
{
    private const int PriorityProcess = 0;

    // QOS_CLASS_USER_INTERACTIVE, sys/qos.h.
    private const uint QosUserInteractive = 0x21;

    // Above everything at the default nice value, and below PipeWire's and the kernel's realtime threads.
    private const int RaisedNice = -10;

    /// <summary>
    /// Raises the calling thread above ordinary work, such as a game on every core, by what each system
    /// honours. On macOS the QoS class, which also keeps the thread on performance cores. On Linux
    /// <see cref="Thread.Priority"/> does nothing, so the thread's nice value is lowered, which needs
    /// CAP_SYS_NICE or an RLIMIT_NICE (<c>LimitNICE=</c> in a systemd unit); without either it keeps the
    /// default, as the outcome is only a less punctual thread.
    /// </summary>
    public static void Raise()
    {
        if (OperatingSystem.IsMacOS())
        {
            // Alone: a thread whose priority was set any other way can no longer take a QoS class.
            _ = pthread_set_qos_class_self_np(QosUserInteractive, 0);
        }
        else if (OperatingSystem.IsLinux())
        {
            // With no target, setpriority takes the calling thread on Linux, not its process.
            _ = setpriority(PriorityProcess, 0, RaisedNice);
        }
        else
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
        }
    }

    [LibraryImport("libSystem", EntryPoint = "pthread_set_qos_class_self_np")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int pthread_set_qos_class_self_np(uint qos, int relativePriority);

    [LibraryImport("libc", EntryPoint = "setpriority")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int setpriority(int which, uint who, int priority);
}
