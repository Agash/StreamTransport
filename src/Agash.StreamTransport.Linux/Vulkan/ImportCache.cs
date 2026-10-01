using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Vortice.Vulkan;

namespace Agash.StreamTransport.Linux.Vulkan;

/// <summary>
/// Imports of producers' DMA-BUF planes, kept across frames. Producers hand over the same few buffers
/// in turn, so a buffer is imported once and reused while it keeps coming back. A buffer is known by
/// its inode, which stays its own while an import holds it, with the plane's place and how it is read.
/// Imports not used for a while are dropped, so a stream that renegotiates does not keep its old
/// buffers alive.
/// </summary>
internal sealed unsafe partial class ImportCache
{
    // Uses after which an import nobody holds is dropped: several turns of any producer's pool.
    private const long Idle = 64;

    private readonly VulkanEngine _engine;
    private readonly Dictionary<Key, Entry> _entries = [];
    private readonly Lock _gate = new();
    private long _uses;

    public ImportCache(VulkanEngine engine) => _engine = engine;

    /// <summary>How many imports are kept.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>An import of a plane, held until the lease is disposed.</summary>
    public Lease Import(
        DmaBufPlane plane,
        ulong modifier,
        VkFormat format,
        int width,
        int height,
        VkImageUsageFlags usage
    )
    {
        Key key = new(
            DmaBufIdentity.Of(plane.Fd),
            plane.Offset,
            plane.Stride,
            modifier,
            format,
            width,
            height,
            usage
        );
        lock (_gate)
        {
            _uses++;
            if (!_entries.TryGetValue(key, out Entry? entry))
            {
                entry = new Entry(
                    VulkanImage.Import(_engine, plane, modifier, format, width, height, usage)
                );
                _entries[key] = entry;
            }

            entry.Holds++;
            entry.LastUse = _uses;
            DropIdle();
            return new Lease(this, entry);
        }
    }

    private void Release(Entry entry)
    {
        lock (_gate)
        {
            entry.Holds--;
        }
    }

    // Called holding the gate.
    private void DropIdle()
    {
        List<Key>? idle = null;
        foreach ((Key key, Entry entry) in _entries)
        {
            if (entry.Holds == 0 && _uses - entry.LastUse > Idle)
            {
                (idle ??= []).Add(key);
            }
        }

        foreach (Key key in idle ?? [])
        {
            _entries.Remove(key, out Entry? entry);
            entry!.Image.Dispose();
        }
    }

    private readonly record struct Key(
        (ulong Device, ulong Inode) Buffer,
        int Offset,
        int Stride,
        ulong Modifier,
        VkFormat Format,
        int Width,
        int Height,
        VkImageUsageFlags Usage
    );

    internal sealed class Entry(VulkanImage image)
    {
        public VulkanImage Image { get; } = image;

        public int Holds { get; set; }

        public long LastUse { get; set; }
    }

    /// <summary>A held import.</summary>
    public readonly struct Lease : IDisposable
    {
        private readonly ImportCache _cache;
        private readonly Entry _entry;

        internal Lease(ImportCache cache, Entry entry)
        {
            _cache = cache;
            _entry = entry;
        }

        public VulkanImage Image => _entry.Image;

        public void Dispose() => _cache.Release(_entry);
    }
}
