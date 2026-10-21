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
                entry = new Entry([
                    VulkanImage.Import(_engine, plane, modifier, format, width, height, usage),
                ]);
                _entries[key] = entry;
            }

            entry.Holds++;
            entry.LastUse = _uses;
            DropIdle();
            return new Lease(this, entry);
        }
    }

    /// <summary>
    /// An import of a multi-planar picture as one image with a view per plane, held until the lease is
    /// disposed: valid for any modifier, tiled ones whose planes depend on each other included.
    /// </summary>
    public Lease ImportPicture(
        in DmaBufImage picture,
        ReadOnlySpan<(VkFormat Format, int Width, int Height)> planes,
        VkImageUsageFlags usage
    )
    {
        Key key = new(
            DmaBufIdentity.Of(picture[0].Fd),
            picture[0].Offset,
            picture[0].Stride,
            picture.Modifier,
            planes[0].Format,
            planes[0].Width,
            planes[0].Height,
            usage,
            planes.Length,
            picture.PlaneCount > 1 ? DmaBufIdentity.Of(picture[1].Fd) : default,
            picture.PlaneCount > 1 ? picture[1].Offset : 0,
            picture.PlaneCount > 1 ? picture[1].Stride : 0
        );
        lock (_gate)
        {
            _uses++;
            if (!_entries.TryGetValue(key, out Entry? entry))
            {
                entry = new Entry(VulkanImage.ImportPicture(_engine, picture, planes, usage));
                _entries[key] = entry;
            }

            entry.Holds++;
            entry.LastUse = _uses;
            DropIdle();
            return new Lease(this, entry);
        }
    }

    internal void Release(Entry entry)
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
            foreach (VulkanImage image in entry!.Images)
            {
                image.Dispose();
            }
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
        VkImageUsageFlags Usage,
        int Planes = 1,
        (ulong Device, ulong Inode) Plane1Buffer = default,
        int Plane1Offset = 0,
        int Plane1Stride = 0
    );

    internal sealed class Entry(VulkanImage[] images)
    {
        public VulkanImage[] Images { get; } = images;

        public int Holds { get; set; }

        public long LastUse { get; set; }
    }

    /// <summary>A held import.</summary>
    public readonly struct Lease : IDisposable
    {
        internal Lease(ImportCache cache, Entry entry)
        {
            Cache = cache;
            Entry = entry;
        }

        public VulkanImage Image => Entry!.Images[0];

        internal ImportCache? Cache { get; }

        internal Entry? Entry { get; }

        /// <summary>A picture import's planes, one view each.</summary>
        public VulkanImage[] Planes => Entry!.Images;

        // A default lease holds nothing and releases nothing.
        public void Dispose() => Cache?.Release(Entry!);
    }
}
