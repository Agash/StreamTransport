using System.Collections.Immutable;
using System.Runtime.Versioning;
using Agash.StreamTransport.Media;
using FFmpeg.Interop.Native;
using static FFmpeg.Interop.D3D11VAExtensions;
using static FFmpeg.Interop.D3D12VAExtensions;
using static FFmpeg.Interop.DrmExtensions;
using static FFmpeg.Interop.VideoToolboxExtensions;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Codecs.FFmpeg;

// Turns a borrowed Media frame into the FFmpeg frame an encoder takes. One per storage kind, chosen
// when the encoder opens on its first frame; the frame's device decides the encoder's device, so an
// input from another GPU than the one it opened on is refused.
internal abstract class EncoderInput : IDisposable
{
    // The pixel format the encoder context is opened with: a hardware format for surface input.
    public abstract FF.PixelFormat ContextFormat { get; }

    // The surfaces the encoder draws from, for surface input.
    public virtual FF.HardwareFramePool? Pool => null;

    // The device the encoder runs on, when it needs one given explicitly.
    public virtual FF.HardwareDevice? Device => null;

    // Fills destination from the frame, sent with a pts; the encoder takes its own reference to what it
    // keeps.
    public abstract void Prepare(in VideoFrame frame, long pts, FF.Frame destination);

    // The encoder has read every frame sent up to and including a pts, whose producers get them back
    // next.
    public virtual void Read(long pts) { }

    public virtual void Dispose() { }
}

// System memory straight into the encoder, for encoders that take it; a hardware encoder that does
// runs on the device given, which it owns.
internal sealed class SystemMemoryInput(PixelFormat format, FF.HardwareDevice? device = null)
    : EncoderInput
{
    public override FF.PixelFormat ContextFormat { get; } = Formats.ToFFmpeg(format);

    public override FF.HardwareDevice? Device => device;

    public override void Prepare(in VideoFrame frame, long pts, FF.Frame destination) =>
        FFmpegFrames.CopyFrom(in frame, destination);

    public override void Dispose() => device?.Dispose();
}

// System memory uploaded into the encoder's surfaces, for encoders that take only surfaces.
internal sealed class UploadInput : EncoderInput
{
    private readonly FF.HardwareDevice _device;
    private readonly FF.HardwareFramePool _pool;
    private readonly FF.Frame _staging = new();

    // Takes ownership of the device.
    public UploadInput(FF.HardwareDevice device, PixelFormat format, VideoSize size)
    {
        _device = device;
        _pool = FF.HardwareFramePool.Create(
            device,
            device.SurfaceFormat,
            Formats.ToFFmpeg(format),
            size.Width,
            size.Height
        );
    }

    public override FF.PixelFormat ContextFormat => _pool.Format;

    public override FF.HardwareFramePool Pool => _pool;

    public override void Prepare(in VideoFrame frame, long pts, FF.Frame destination)
    {
        FFmpegFrames.CopyFrom(in frame, _staging);
        _pool.Upload(_staging, destination);
    }

    public override void Dispose()
    {
        _staging.Dispose();
        _pool.Dispose();
        _device.Dispose();
    }
}

// A Direct3D 11 texture handed to NVENC or AMF on the texture's device: read in place when it is
// exactly the picture, copied on the GPU into the encoder's surfaces when it is larger.
[SupportedOSPlatform("windows6.1")]
internal sealed unsafe class D3D11Input : EncoderInput
{
    private const uint BindRenderTarget = 0x20;
    private const uint BindShaderResource = 0x8;
    private const uint BindDecoder = 0x200;

    private readonly FF.HardwareDevice _device;
    private readonly FF.HardwareFramePool _pool;
    private readonly VideoSize _size;

    public D3D11Input(D3D11Image image, PixelFormat format, VideoSize size)
    {
        _size = size;
        _device = FF.HardwareDevice.FromD3D11Texture(image.Texture);
        // A fixed texture array, which NVENC and AMF register once. Direct3D 11 needs a bind flag for
        // one: YUV surfaces are bound as decoder targets, RGB as render targets the encoder samples.
        FF.PixelFormat software = Formats.SurfaceSoftwareFormat(format);
        uint bind =
            software == FF.PixelFormat.Bgra ? BindRenderTarget | BindShaderResource : BindDecoder;
        _pool = FF.HardwareFramePool.Create(
            _device,
            FF.PixelFormat.D3D11,
            software,
            size.Width,
            size.Height,
            initialSize: 8,
            configure: pool => ((AVD3D11VAFramesContext*)pool.Context->hwctx)->BindFlags = bind
        );
    }

    public override FF.PixelFormat ContextFormat => FF.PixelFormat.D3D11;

    public override FF.HardwareFramePool Pool => _pool;

    public override FF.HardwareDevice Device => _device;

    public override void Prepare(in VideoFrame frame, long pts, FF.Frame destination)
    {
        if (!frame.Storage.TryGetValue(out D3D11Image image))
        {
            throw new ArgumentException("The frame is not a Direct3D 11 texture.", nameof(frame));
        }

        if (image.Sync.Kind != D3D11SyncKind.None)
        {
            throw new NotSupportedException(
                $"Direct3D 11 frames synchronised by {image.Sync.Kind} are not supported; deliver them finished."
            );
        }

        // NVENC and AMF read any texture on their device, so a texture of exactly the picture is read in
        // place; a larger one (a decoder pads its surfaces) is copied, since the encoder reads it whole.
        if (frame.Format.CodedSize == _size)
        {
            _pool.WrapD3D11Texture(image.Texture, image.Subresource, destination);
        }
        else
        {
            _pool.CopyFromD3D11Texture(image.Texture, image.Subresource, destination);
        }
    }

    public override void Dispose()
    {
        _pool.Dispose();
        _device.Dispose();
    }
}

// A Direct3D 12 resource handed to a Direct3D 12 encoder as it is, ordered after its producer's queue.
[SupportedOSPlatform("windows10.0.10240")]
internal sealed class D3D12Input : EncoderInput
{
    private readonly FF.HardwareDevice _device;
    private readonly FF.HardwareFramePool _pool;
    private readonly VideoSize _size;

    public D3D12Input(D3D12Image image, PixelFormat format, VideoSize size)
    {
        _size = size;
        _device = FF.HardwareDevice.FromD3D12Resource(image.Resource);
        _pool = FF.HardwareFramePool.Create(
            _device,
            FF.PixelFormat.D3D12,
            Formats.SurfaceSoftwareFormat(format),
            size.Width,
            size.Height
        );
    }

    public override FF.PixelFormat ContextFormat => FF.PixelFormat.D3D12;

    public override FF.HardwareFramePool Pool => _pool;

    public override FF.HardwareDevice Device => _device;

    // A texture of exactly the picture is read in place; a larger one (a decoder pads its surfaces) or a
    // slice of an array is first copied on the GPU, since the encoder reads whole single textures.
    // Either way the encoder is ordered after the producer on the GPU.
    public override void Prepare(in VideoFrame frame, long pts, FF.Frame destination)
    {
        if (!frame.Storage.TryGetValue(out D3D12Image image))
        {
            throw new ArgumentException("The frame is not a Direct3D 12 resource.", nameof(frame));
        }

        bool inPlace = image.Subresource == 0 && frame.Format.CodedSize == _size;
        D3D12Sync sync = image.Sync;
        if (inPlace && sync.Fence != 0 && sync.ProducerQueue == 0)
        {
            _pool.WrapD3D12Texture(image.Resource, sync.Fence, sync.Value, destination);
        }
        else if (inPlace)
        {
            _pool.WrapD3D12Texture(image.Resource, sync.ProducerQueue, destination);
        }
        else if (sync.ProducerQueue == 0)
        {
            _pool.CopyFromD3D12Texture(
                image.Resource,
                image.Subresource,
                sync.Fence,
                sync.Value,
                destination
            );
        }
        else
        {
            throw new NotSupportedException(
                "A Direct3D 12 frame larger than the picture is copied, and the copy is ordered by a fence: give the frame's fence."
            );
        }
    }

    public override void Dispose()
    {
        _pool.Dispose();
        _device.Dispose();
    }
}

// A DMA-BUF imported as DRM PRIME and mapped into the encoder's VA-API surfaces, which take the
// buffer's memory as it is.
[SupportedOSPlatform("linux")]
internal sealed class DmaBufInput : EncoderInput
{
    private readonly FF.HardwareDevice _device;
    private readonly FF.HardwareFramePool _pool;
    private readonly VideoSize _size;

    // Takes ownership of the device.
    public DmaBufInput(FF.HardwareDevice device, PixelFormat format, VideoSize size)
    {
        _device = device;
        _size = size;
        _pool = FF.HardwareFramePool.Create(
            device,
            device.SurfaceFormat,
            Formats.ToFFmpeg(format),
            size.Width,
            size.Height
        );
    }

    public override FF.PixelFormat ContextFormat => _pool.Format;

    public override FF.HardwareFramePool Pool => _pool;

    public override void Prepare(in VideoFrame frame, long pts, FF.Frame destination)
    {
        using var imported = FF.Frame.FromDrmPrime(
            DmaBufs.ToDrmPrime(DmaBufs.Finished(in frame)),
            _size.Width,
            _size.Height
        );
        imported.MapTo(_pool, destination, FF.HardwareMapAccess.Read);
    }

    public override void Dispose()
    {
        _pool.Dispose();
        _device.Dispose();
    }
}

// A DMA-BUF handed to a Vulkan Video encoder in place: its memory is imported as an image made for the
// encoder's input, taken from its producer while the encoder reads it and given back once the encoder
// has. Producers cycle a few buffers, so each buffer's import is kept while it keeps coming back. A buffer
// whose layout this GPU cannot encode from is mapped into Vulkan and copied on the GPU instead.
[SupportedOSPlatform("linux")]
internal sealed class VulkanDmaBufInput : EncoderInput
{
    // Imports kept for buffers not in use: a producer's pool is a handful of buffers.
    private const int KeptImports = 16;

    private readonly FF.HardwareDevice _device;
    private readonly FF.HardwareFramePool _pool;
    private readonly FF.VulkanDmaBufImporter _importer;
    private readonly VideoSize _size;
    private readonly Dictionary<ImportKey, Import> _imports = [];
    private readonly Queue<(long Pts, Import Import)> _reading = new();
    private readonly Action<ulong> _copying;
    private readonly Dictionary<ulong, bool> _inPlace = [];
    private FF.HardwareFramePool? _mappings;
    private long _uses;

    // Takes ownership of the device; copying is told once for each modifier the GPU cannot read in place.
    public VulkanDmaBufInput(
        FF.HardwareDevice device,
        PixelFormat format,
        VideoSize size,
        Action<ulong> copying
    )
    {
        _device = device;
        _size = size;
        _copying = copying;
        _pool = FF.HardwareFramePool.Create(
            device,
            FF.PixelFormat.Vulkan,
            Formats.ToFFmpeg(format),
            size.Width,
            size.Height
        );
        _importer = new FF.VulkanDmaBufImporter(_pool);
    }

    public override FF.PixelFormat ContextFormat => FF.PixelFormat.Vulkan;

    public override FF.HardwareFramePool Pool => _pool;

    public override void Prepare(in VideoFrame frame, long pts, FF.Frame destination)
    {
        DmaBufImage image = DmaBufs.Finished(in frame);
        FF.DrmPrimeImage picture = DmaBufs.ToDrmPrime(image);
        if (picture.Objects.Length != 1)
        {
            throw new NotSupportedException(
                $"A picture in {picture.Objects.Length} DMA-BUFs cannot be encoded: Vulkan reads a picture from one buffer."
            );
        }

        if (!_inPlace.TryGetValue(image.Modifier, out bool inPlace))
        {
            inPlace = _importer.Supports(image.Modifier);
            _inPlace.Add(image.Modifier, inPlace);
            if (!inPlace)
            {
                _copying(image.Modifier);
            }
        }

        if (!inPlace)
        {
            Copy(picture, destination);
            return;
        }

        var key = ImportKey.Of(image, picture);
        if (!_imports.TryGetValue(key, out Import? import))
        {
            Evict();
            import = new Import();
            _importer.Import(picture, _size.Width, _size.Height, import.Frame);
            _imports.Add(key, import);
        }

        if (import.Reading++ == 0)
        {
            _importer.Acquire(import.Frame);
        }

        import.LastUse = ++_uses;
        _reading.Enqueue((pts, import));
        destination.Reference(import.Frame);
    }

    public override void Read(long pts)
    {
        while (_reading.TryPeek(out (long Pts, Import Import) oldest) && oldest.Pts <= pts)
        {
            _ = _reading.Dequeue();
            if (--oldest.Import.Reading == 0)
            {
                _importer.Release(oldest.Import.Frame);
            }
        }
    }

    public override void Dispose()
    {
        // The encoder is closed: whatever it was still reading goes back to its producer.
        Read(long.MaxValue);
        foreach (Import import in _imports.Values)
        {
            import.Frame.Dispose();
        }

        _imports.Clear();
        _importer.Dispose();
        _mappings?.Dispose();
        _pool.Dispose();
        _device.Dispose();
    }

    // FFmpeg maps the buffer into an image the GPU can copy from; the copy lands in a surface of the
    // encoder's own pool, and finishes before the mapping goes.
    private void Copy(FF.DrmPrimeImage picture, FF.Frame destination)
    {
        _mappings ??= FF.HardwareFramePool.Create(
            _device,
            FF.PixelFormat.Vulkan,
            _pool.SoftwareFormat,
            _size.Width,
            _size.Height
        );
        using var imported = FF.Frame.FromDrmPrime(picture, _size.Width, _size.Height);
        using FF.Frame mapped = new();
        imported.MapTo(_mappings, mapped, FF.HardwareMapAccess.Read);
        _pool.GetFrame(destination);
        mapped.CopyTo(destination);
    }

    // Drops the least recently used import no frame is reading, once the producer has shown more buffers
    // than a pool holds.
    private void Evict()
    {
        if (_imports.Count < KeptImports)
        {
            return;
        }

        KeyValuePair<ImportKey, Import>? oldest = null;
        foreach (KeyValuePair<ImportKey, Import> entry in _imports)
        {
            if (
                entry.Value.Reading == 0
                && (oldest is null || entry.Value.LastUse < oldest.Value.Value.LastUse)
            )
            {
                oldest = entry;
            }
        }

        if (oldest is { } evicted)
        {
            _ = _imports.Remove(evicted.Key);
            evicted.Value.Frame.Dispose();
        }
    }

    private sealed class Import
    {
        public FF.Frame Frame { get; } = new();

        // Frames sent to the encoder from this buffer that it has not finished reading.
        public int Reading { get; set; }

        public long LastUse { get; set; }
    }

    // A buffer and the layout read from it: the same DMA-BUF described another way is another import.
    private readonly record struct ImportKey(
        (ulong Device, ulong Inode) Buffer,
        ulong Modifier,
        long Plane0,
        long Pitch0,
        long Plane1,
        long Pitch1
    )
    {
        public static ImportKey Of(DmaBufImage image, FF.DrmPrimeImage picture)
        {
            ImmutableArray<FF.DrmPlane> planes = picture.Layers[0].Planes;
            return new(
                DmaBufIdentity.Of(picture.Objects[0].FileDescriptor),
                image.Modifier,
                planes[0].Offset,
                planes[0].Pitch,
                planes.Length > 1 ? planes[1].Offset : -1,
                planes.Length > 1 ? planes[1].Pitch : -1
            );
        }
    }
}

// Media's DMA-BUF pictures as FFmpeg describes them.
[SupportedOSPlatform("linux")]
internal static class DmaBufs
{
    // The frame's picture, which must be finished when it is delivered.
    public static DmaBufImage Finished(in VideoFrame frame)
    {
        if (!frame.Storage.TryGetValue(out DmaBufImage image))
        {
            throw new ArgumentException("The frame is not a DMA-BUF.", nameof(frame));
        }

        if (image.Sync is not null)
        {
            throw new NotSupportedException(
                "DMA-BUF frames with explicit sync timelines are not supported yet; deliver them finished."
            );
        }

        return image;
    }

    // One layer in the picture's DRM format. Planes in one DMA-BUF are one object, whichever descriptor
    // each came with: importers take frames made of a single object, and a producer may hand every plane
    // its own descriptor.
    public static FF.DrmPrimeImage ToDrmPrime(DmaBufImage image)
    {
        var objects = ImmutableArray.CreateBuilder<FF.DrmObject>(image.PlaneCount);
        var identities = new List<(ulong, ulong)>(image.PlaneCount);
        var planes = ImmutableArray.CreateBuilder<FF.DrmPlane>(image.PlaneCount);
        for (int i = 0; i < image.PlaneCount; i++)
        {
            DmaBufPlane plane = image[i];
            (ulong, ulong) identity = DmaBufIdentity.Of(plane.Fd);
            int index = identities.IndexOf(identity);
            if (index < 0)
            {
                index = objects.Count;
                identities.Add(identity);
                objects.Add(new FF.DrmObject(plane.Fd, 0, image.Modifier));
            }

            planes.Add(new FF.DrmPlane(index, plane.Offset, plane.Stride));
        }

        return new FF.DrmPrimeImage(
            objects.DrainToImmutable(),
            [new FF.DrmLayer(image.DrmFormat, planes.DrainToImmutable())]
        );
    }
}

// An IOSurface wrapped as a VideoToolbox frame without a copy.
[SupportedOSPlatform("macos")]
internal sealed class IOSurfaceInput : EncoderInput
{
    private readonly FF.HardwareDevice _device;
    private readonly FF.HardwareFramePool _pool;

    // Takes ownership of the device.
    public IOSurfaceInput(FF.HardwareDevice device, PixelFormat format, VideoSize size)
    {
        _device = device;
        _pool = FF.HardwareFramePool.Create(
            device,
            FF.PixelFormat.VideoToolbox,
            Formats.ToFFmpeg(format),
            size.Width,
            size.Height
        );
    }

    public override FF.PixelFormat ContextFormat => FF.PixelFormat.VideoToolbox;

    public override FF.HardwareFramePool Pool => _pool;

    public override void Prepare(in VideoFrame frame, long pts, FF.Frame destination)
    {
        if (!frame.Storage.TryGetValue(out IOSurfaceImage image))
        {
            throw new ArgumentException("The frame is not an IOSurface.", nameof(frame));
        }

        if (image.SharedEvent != 0)
        {
            throw new NotSupportedException(
                "IOSurface frames synchronised by a shared event are not supported yet; deliver them finished."
            );
        }

        _pool.WrapIOSurface(image.Surface, destination);
    }

    public override void Dispose()
    {
        _pool.Dispose();
        _device.Dispose();
    }
}
