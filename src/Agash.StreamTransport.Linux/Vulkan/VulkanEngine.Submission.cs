using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Vortice.Vulkan;

namespace Agash.StreamTransport.Linux.Vulkan;

// Submission: batches run without the CPU waiting for them. Each signals the engine's timeline, which
// is exported as a DRM syncobj so frames carry the point their pictures are ready at; what a batch held
// is let go by a completion thread once its point is reached.
internal sealed unsafe partial class VulkanEngine
{
    // Command buffers in flight at most; a batch beyond them waits for the oldest.
    private const int MaxInFlight = 8;

    private readonly List<Slot> _slots = [];
    private readonly Lock _pendingGate = new();
    private readonly Queue<(ulong Value, Action Release)> _pending = new();
    private readonly SemaphoreSlim _pendingAdded = new(0);
    private VkSemaphore _timeline;
    private ulong _submitted;

    /// <summary>The engine's timeline as a DRM syncobj, which frames' sync points are on.</summary>
    public int TimelineSyncobj { get; private set; } = -1;

    /// <summary>The sync point a frame written by a batch is ready at.</summary>
    public DrmSyncTimeline Ready(ulong value) => new(TimelineSyncobj, value, -1, 0);

    /// <summary>The value the GPU has reached on the engine's timeline.</summary>
    public ulong Completed
    {
        get
        {
            ulong value;
            Api.vkGetSemaphoreCounterValue(_timeline, &value).CheckResult();
            return value;
        }
    }

    /// <summary>Blocks until the GPU reaches a value: for a reader on the CPU.</summary>
    public void Wait(ulong value)
    {
        VkSemaphore timeline = _timeline;
        VkSemaphoreWaitInfo info = new()
        {
            semaphoreCount = 1,
            pSemaphores = &timeline,
            pValues = &value,
        };
        Api.vkWaitSemaphores(&info, ulong.MaxValue).CheckResult();
    }

    private void CreateTimeline()
    {
        VkExportSemaphoreCreateInfo export = new()
        {
            handleTypes = VkExternalSemaphoreHandleTypeFlags.OpaqueFD,
        };
        VkSemaphoreTypeCreateInfo type = new()
        {
            pNext = &export,
            semaphoreType = VkSemaphoreType.Timeline,
            initialValue = 0,
        };
        VkSemaphoreCreateInfo info = new() { pNext = &type };
        VkSemaphore timeline;
        Api.vkCreateSemaphore(&info, null, &timeline).CheckResult();
        _timeline = timeline;
        VkSemaphoreGetFdInfoKHR get = new()
        {
            semaphore = timeline,
            handleType = VkExternalSemaphoreHandleTypeFlags.OpaqueFD,
        };
        int fd;
        Api.vkGetSemaphoreFdKHR(&get, &fd).CheckResult();
        TimelineSyncobj = fd;
        Thread completion = new(CompleteBatches)
        {
            IsBackground = true,
            Name = $"Vulkan completion {Identity}",
        };
        completion.Start();
    }

    /// <summary>
    /// Starts one batch of GPU work. Recording is serialised: the batch holds the engine until it is
    /// submitted or disposed.
    /// </summary>
    /// <returns>The batch; record into <see cref="Batch.Commands"/>, then submit it.</returns>
    public Batch Begin()
    {
        _gate.Enter();
        try
        {
            Slot slot = FreeSlot();
            Api.vkResetCommandBuffer(slot.Commands, 0).CheckResult();
            VkCommandBufferBeginInfo begin = new()
            {
                flags = VkCommandBufferUsageFlags.OneTimeSubmit,
            };
            Api.vkBeginCommandBuffer(slot.Commands, &begin).CheckResult();
            _recording = slot;
            return new Batch(this);
        }
        catch
        {
            _gate.Exit();
            throw;
        }
    }

    // A command buffer the GPU is done with, a new one while fewer than the cap are in flight, or the
    // oldest once it completes. Called holding the gate.
    private Slot FreeSlot()
    {
        ulong completed = Completed;
        foreach (Slot slot in _slots)
        {
            if (slot.Value <= completed)
            {
                return slot;
            }
        }

        if (_slots.Count < MaxInFlight)
        {
            VkCommandBufferAllocateInfo allocate = new()
            {
                commandPool = _commandPool,
                level = VkCommandBufferLevel.Primary,
                commandBufferCount = 1,
            };
            VkCommandBuffer commands;
            Api.vkAllocateCommandBuffers(&allocate, &commands).CheckResult();
            Slot added = new(commands);
            _slots.Add(added);
            return added;
        }

        Slot oldest = _slots.MinBy(slot => slot.Value)!;
        Wait(oldest.Value);
        return oldest;
    }

    // Runs releases in value order as the GPU reaches them; a dedicated thread blocked in the driver,
    // the Vulkan counterpart of Metal's completion handlers.
    private void CompleteBatches()
    {
        while (true)
        {
            _pendingAdded.Wait();
            (ulong Value, Action Release) next;
            lock (_pendingGate)
            {
                next = _pending.Dequeue();
            }

            Wait(next.Value);
            next.Release();
        }
    }

    // Submits the recording slot: waits on what the batch named, signals the next timeline value.
    // Called holding the gate.
    private ulong Execute(Slot slot)
    {
        Api.vkEndCommandBuffer(slot.Commands).CheckResult();
        ulong value = ++_submitted;
        int waits = slot.Waits.Count;
        VkSemaphore* waitSemaphores = stackalloc VkSemaphore[Math.Max(waits, 1)];
        ulong* waitValues = stackalloc ulong[Math.Max(waits, 1)];
        VkPipelineStageFlags* stages = stackalloc VkPipelineStageFlags[Math.Max(waits, 1)];
        for (int i = 0; i < waits; i++)
        {
            waitSemaphores[i] = slot.Waits[i].Semaphore;
            waitValues[i] = slot.Waits[i].Value;
            stages[i] = VkPipelineStageFlags.AllCommands;
        }

        VkSemaphore timeline = _timeline;
        VkTimelineSemaphoreSubmitInfo values = new()
        {
            waitSemaphoreValueCount = (uint)waits,
            pWaitSemaphoreValues = waitValues,
            signalSemaphoreValueCount = 1,
            pSignalSemaphoreValues = &value,
        };
        VkCommandBuffer commands = slot.Commands;
        VkSubmitInfo submit = new()
        {
            pNext = &values,
            waitSemaphoreCount = (uint)waits,
            pWaitSemaphores = waitSemaphores,
            pWaitDstStageMask = stages,
            commandBufferCount = 1,
            pCommandBuffers = &commands,
            signalSemaphoreCount = 1,
            pSignalSemaphores = &timeline,
        };
        Api.vkQueueSubmit(_queue, 1, &submit, VkFence.Null).CheckResult();
        slot.Value = value;

        // Semaphores imported for this batch's waits are destroyed once it has run.
        if (waits > 0)
        {
            VkSemaphore[] imported = [.. slot.Waits.Select(wait => wait.Semaphore)];
            slot.Releases.Add(() =>
            {
                foreach (VkSemaphore semaphore in imported)
                {
                    Api.vkDestroySemaphore(semaphore, null);
                }
            });
        }

        slot.Waits.Clear();
        if (slot.Releases.Count > 0)
        {
            Action[] releases = [.. slot.Releases];
            slot.Releases.Clear();
            lock (_pendingGate)
            {
                _pending.Enqueue(
                    (
                        value,
                        () =>
                        {
                            foreach (Action release in releases)
                            {
                                release();
                            }
                        }
                    )
                );
            }

            _ = _pendingAdded.Release();
        }

        return value;
    }

    // A wait on a producer's sync point, imported into a timeline semaphore of this device.
    private VkSemaphore ImportTimeline(int syncobj)
    {
        VkSemaphoreTypeCreateInfo type = new() { semaphoreType = VkSemaphoreType.Timeline };
        VkSemaphoreCreateInfo info = new() { pNext = &type };
        VkSemaphore semaphore;
        Api.vkCreateSemaphore(&info, null, &semaphore).CheckResult();

        // The import takes the descriptor; the frame keeps its own.
        int fd = Dup(syncobj);
        if (fd < 0)
        {
            Api.vkDestroySemaphore(semaphore, null);
            throw new InvalidOperationException(
                $"The frame's syncobj could not be duplicated (errno {Marshal.GetLastPInvokeError()})."
            );
        }

        VkImportSemaphoreFdInfoKHR import = new()
        {
            semaphore = semaphore,
            handleType = VkExternalSemaphoreHandleTypeFlags.OpaqueFD,
            fd = fd,
        };
        VkResult result = Api.vkImportSemaphoreFdKHR(&import);
        if (result != VkResult.Success)
        {
            _ = Close(fd);
            Api.vkDestroySemaphore(semaphore, null);
            result.CheckResult();
        }

        return semaphore;
    }

    // The fences a DMA-BUF's implicit-sync users left, as a binary semaphore a batch waits on; null when
    // the kernel cannot export them (before Linux 6.0), and the reader then waits on the CPU.
    private VkSemaphore? ImportFences(int dmabuf, bool write)
    {
        DmaBufSyncFile export = new() { Flags = write ? SyncReadWrite : SyncRead, Fd = -1 };
        if (Ioctl(dmabuf, ExportSyncFile, &export) != 0)
        {
            return null;
        }

        VkSemaphoreCreateInfo info = new();
        VkSemaphore semaphore;
        Api.vkCreateSemaphore(&info, null, &semaphore).CheckResult();
        VkImportSemaphoreFdInfoKHR import = new()
        {
            semaphore = semaphore,
            flags = VkSemaphoreImportFlags.Temporary,
            handleType = VkExternalSemaphoreHandleTypeFlags.SyncFD,
            fd = export.Fd,
        };
        VkResult result = Api.vkImportSemaphoreFdKHR(&import);
        if (result != VkResult.Success)
        {
            _ = Close(export.Fd);
            Api.vkDestroySemaphore(semaphore, null);
            return null;
        }

        return semaphore;
    }

    /// <summary>
    /// One batch of recorded GPU work, holding the engine until it ends. The batch in progress is the
    /// engine's, so a copy of this view (a <c>using</c> variable is read-only) ends the same batch.
    /// </summary>
    public readonly ref struct Batch
    {
        private readonly VulkanEngine _engine;

        internal Batch(VulkanEngine engine) => _engine = engine;

        public VkCommandBuffer Commands => Current.Commands;

        private Slot Current =>
            _engine._recording ?? throw new InvalidOperationException("The batch has ended.");

        /// <summary>
        /// Makes the batch wait on the GPU for the producer of a picture it reads, or, to write it, for
        /// every user of it: on the frame's sync point when it carries one, otherwise on the DMA-BUF's
        /// implicit fences. A picture this engine wrote needs nothing: its queue runs in order.
        /// </summary>
        /// <returns>False when the kernel cannot export the fences and they did not clear in time.</returns>
        public bool WaitFor(in DmaBufImage image, bool write = false)
        {
            Slot slot = Current;
            if (image.Sync is { } sync)
            {
                if (sync.AcquireSyncobj != _engine.TimelineSyncobj)
                {
                    slot.Waits.Add(
                        (_engine.ImportTimeline(sync.AcquireSyncobj), sync.AcquirePoint)
                    );
                }

                return true;
            }

            int previous = -1;
            for (int i = 0; i < image.PlaneCount; i++)
            {
                int fd = image[i].Fd;
                if (fd == previous)
                {
                    continue;
                }

                previous = fd;
                if (_engine.ImportFences(fd, write) is { } fences)
                {
                    slot.Waits.Add((fences, 0));
                }
                else if (!Recording.WaitForWriters(fd, WriterTimeout))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Lets go of something once the GPU has run the batch.</summary>
        public void Then(Action release) => Current.Releases.Add(release);

        /// <summary>Runs the batch without waiting for it.</summary>
        /// <returns>The timeline value the batch signals when it has run.</returns>
        public ulong Submit()
        {
            Slot slot = Current;
            _engine._recording = null;
            try
            {
                return _engine.Execute(slot);
            }
            finally
            {
                _engine._gate.Exit();
            }
        }

        /// <summary>Runs the batch and waits until the GPU has run it: for a reader on the CPU.</summary>
        public void SubmitAndWait() => _engine.Wait(Submit());

        /// <summary>Ends a batch that was not submitted, discarding what was recorded.</summary>
        public void Dispose()
        {
            if (_engine._recording is { } slot)
            {
                _engine._recording = null;
                _ = _engine.Api.vkEndCommandBuffer(slot.Commands);
                foreach ((VkSemaphore semaphore, _) in slot.Waits)
                {
                    _engine.Api.vkDestroySemaphore(semaphore, null);
                }

                slot.Waits.Clear();
                foreach (Action release in slot.Releases)
                {
                    release();
                }

                slot.Releases.Clear();
                _engine._gate.Exit();
            }
        }
    }

    // A command buffer and what the batch recorded into it waits on and holds.
    private sealed class Slot(VkCommandBuffer commands)
    {
        public VkCommandBuffer Commands { get; } = commands;

        public ulong Value { get; set; }

        public List<(VkSemaphore Semaphore, ulong Value)> Waits { get; } = [];

        public List<Action> Releases { get; } = [];
    }

    private static readonly TimeSpan WriterTimeout = TimeSpan.FromSeconds(1);

    // linux/dma-buf.h: DMA_BUF_IOCTL_EXPORT_SYNC_FILE and its flags.
    private const nuint ExportSyncFile = 0xC0086202;
    private const uint SyncRead = 1;
    private const uint SyncReadWrite = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct DmaBufSyncFile
    {
        public uint Flags;
        public int Fd;
    }

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(int fd, nuint request, void* argument);

    [LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static partial int Dup(int fd);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);
}
