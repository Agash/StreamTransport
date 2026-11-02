using System.Runtime.InteropServices;

namespace Agash.StreamTransport.Threading;

/// <summary>
/// What a dedicated thread waits on: until a deadline or a signal. A timer callback reaches its waiter
/// through the thread pool, which under load adds tens of milliseconds, so on the system clock the thread
/// sleeps in the kernel itself: on a high-resolution waitable timer on Windows, whose wake is independent
/// of the system tick, and on an event elsewhere. A clock of another kind (a test's) keeps its own time and
/// wakes the thread through its timers.
/// </summary>
internal sealed partial class DeadlineSignal : IDisposable
{
    private const uint CreateHighResolution = 2;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint Infinite = 0xFFFFFFFF;

    // How long a wait with no deadline sleeps on a waitable timer, which always has a due time.
    private static readonly TimeSpan Idle = TimeSpan.FromHours(1);

    private readonly TimeProvider _time;
    private readonly Func<TimeSpan> _now;
    private readonly ManualResetEventSlim _event = new();
    private readonly nint _waitable;
    private ITimer? _timer;
    private int _signalled;

    /// <summary>A signal on a clock.</summary>
    /// <param name="time">The clock's time provider.</param>
    /// <param name="now">Reads the clock deadlines are given on.</param>
    public DeadlineSignal(TimeProvider time, Func<TimeSpan> now)
    {
        _time = time;
        _now = now;
        if (OperatingSystem.IsWindows() && time == TimeProvider.System)
        {
            _waitable = CreateWaitableTimerExW(0, 0, CreateHighResolution, TimerAllAccess);
        }
    }

    /// <summary>Ends the current wait, or the next one if none is running.</summary>
    public void Signal()
    {
        Volatile.Write(ref _signalled, 1);
        if (_waitable != 0)
        {
            long immediately = -1;
            _ = SetWaitableTimer(_waitable, in immediately, 0, 0, 0, false);
        }
        else
        {
            _event.Set();
        }
    }

    /// <summary>Waits for a signal or for the clock to reach the deadline; may return early.</summary>
    /// <param name="deadline">The deadline on the clock, or null for no deadline.</param>
    public void WaitUntil(TimeSpan? deadline)
    {
        if (Interlocked.Exchange(ref _signalled, 0) == 1)
        {
            return;
        }

        TimeSpan? remaining = deadline - _now();
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        if (_waitable != 0)
        {
            long due = -(remaining ?? Idle).Ticks;
            _ = SetWaitableTimer(_waitable, in due, 0, 0, 0, false);

            // A signal before the timer was armed above was overwritten by it, and left its flag.
            if (Volatile.Read(ref _signalled) == 0)
            {
                _ = WaitForSingleObject(_waitable, Infinite);
            }

            return;
        }

        if (_time == TimeProvider.System)
        {
            _ = _event.Wait(remaining ?? Timeout.InfiniteTimeSpan);
        }
        else
        {
            _timer ??= _time.CreateTimer(
                static state => ((DeadlineSignal)state!).Signal(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan
            );
            _ = _timer.Change(remaining ?? Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            // Time that passed while the timer was being armed is not in its due time; the clock says.
            if (deadline is not { } end || _now() < end)
            {
                _event.Wait();
            }
        }

        // A signal after this reset left its flag, which the next wait reads first.
        _event.Reset();
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _event.Dispose();
        if (_waitable != 0)
        {
            _ = CloseHandle(_waitable);
        }
    }

    [LibraryImport("kernel32", EntryPoint = "CreateWaitableTimerExW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateWaitableTimerExW(
        nint attributes,
        nint name,
        uint flags,
        uint access
    );

    [LibraryImport("kernel32", EntryPoint = "SetWaitableTimer")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(
        nint timer,
        in long due,
        int period,
        nint completion,
        nint argument,
        [MarshalAs(UnmanagedType.Bool)] bool resume
    );

    [LibraryImport("kernel32", EntryPoint = "WaitForSingleObject")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32", EntryPoint = "CloseHandle")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
