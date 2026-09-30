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
// input from another GPU than the one it opened on is refused rather than copied across adapters.
internal abstract class EncoderInput : IDisposable
{
    // The pixel format the encoder context is opened with: a hardware format for surface input.
    public abstract FF.PixelFormat ContextFormat { get; }

    // The surfaces the encoder draws from, for surface input.
    public virtual FF.HardwareFramePool? Pool => null;

    // The device the encoder runs on, when it needs one given explicitly.
    public virtual FF.HardwareDevice? Device => null;

    // Fills destination from the frame; the encoder takes its own reference to what it keeps.
    public abstract void Prepare(in VideoFrame frame, FF.Frame destination);

    public virtual void Dispose() { }

    // Copies a CPU frame's planes into an FFmpeg frame allocated for them.
    public static void CopyCpu(in VideoFrame frame, FF.Frame destination)
    {
        if (!frame.Storage.TryGetValue(out CpuImage image))
        {
            throw new ArgumentException("The frame is not in CPU memory.", nameof(frame));
        }

        VideoSize size = new(frame.Format.VisibleRect.Width, frame.Format.VisibleRect.Height);
        destination.AllocateVideo(
            size.Width,
            size.Height,
            Formats.ToFFmpeg(frame.Format.PixelFormat)
        );
        for (int plane = 0; plane < image.Planes.Count; plane++)
        {
            ReadOnlySpan<byte> source = frame.GetPlane(plane);
            int stride = image.Planes[plane].Stride;
            FF.ImagePlane target = destination.GetWritablePlane(plane);
            int rowLength = target.RowLength;
            int top = RowOffset(frame.Format.PixelFormat, plane, frame.Format.VisibleRect.Y);
            int left = ByteOffset(frame.Format.PixelFormat, plane, frame.Format.VisibleRect.X);
            for (int row = 0; row < target.Height; row++)
            {
                source.Slice(((top + row) * stride) + left, rowLength).CopyTo(target.GetRow(row));
            }
        }
    }

    // The first row of a plane for a visible rectangle starting at luma row y.
    private static int RowOffset(PixelFormat format, int plane, int y) =>
        plane > 0 && format is PixelFormat.Nv12 or PixelFormat.P010 or PixelFormat.I420 ? y / 2 : y;

    // The first byte of a row for a visible rectangle starting at luma column x.
    private static int ByteOffset(PixelFormat format, int plane, int x) =>
        format switch
        {
            PixelFormat.Nv12 => x,
            PixelFormat.P010 => 2 * x,
            PixelFormat.I420 => plane == 0 ? x : x / 2,
            _ => 4 * x,
        };
}

// System memory straight into the encoder, for encoders that take it; a hardware encoder that does
// runs on the device given, which it owns.
internal sealed class SystemMemoryInput(PixelFormat format, FF.HardwareDevice? device = null)
    : EncoderInput
{
    public override FF.PixelFormat ContextFormat { get; } = Formats.ToFFmpeg(format);

    public override FF.HardwareDevice? Device => device;

    public override void Prepare(in VideoFrame frame, FF.Frame destination) =>
        CopyCpu(in frame, destination);

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

    public override void Prepare(in VideoFrame frame, FF.Frame destination)
    {
        CopyCpu(in frame, _staging);
        _pool.Upload(_staging, destination);
    }

    public override void Dispose()
    {
        _staging.Dispose();
        _pool.Dispose();
        _device.Dispose();
    }
}

// A Direct3D 11 texture copied on the GPU into the encoder's own surfaces, on the texture's device:
// NVENC, AMF and QSV take only surfaces from their pool.
[SupportedOSPlatform("windows6.1")]
internal sealed unsafe class D3D11Input : EncoderInput
{
    private const uint BindRenderTarget = 0x20;
    private const uint BindShaderResource = 0x8;
    private const uint BindDecoder = 0x200;

    private readonly FF.HardwareDevice _device;
    private readonly FF.HardwareFramePool _pool;

    public D3D11Input(D3D11Image image, PixelFormat format, VideoSize size)
    {
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

    public override void Prepare(in VideoFrame frame, FF.Frame destination)
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

        _pool.CopyFromD3D11Texture(image.Texture, image.Subresource, destination);
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
    public override void Prepare(in VideoFrame frame, FF.Frame destination)
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
                "A Direct3D 12 frame larger than the picture must be ordered by a fence, not a producer queue."
            );
        }
    }

    public override void Dispose()
    {
        _pool.Dispose();
        _device.Dispose();
    }
}

// A DMA-BUF imported as DRM PRIME and mapped into the encoder's VA-API or Vulkan surfaces.
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

    public override void Prepare(in VideoFrame frame, FF.Frame destination)
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

        var objects = new FF.DrmObject[image.PlaneCount];
        var planes = new FF.DrmPlane[image.PlaneCount];
        for (int i = 0; i < image.PlaneCount; i++)
        {
            DmaBufPlane plane = image[i];
            objects[i] = new FF.DrmObject(plane.Fd, 0, image.Modifier);
            planes[i] = new FF.DrmPlane(i, plane.Offset, plane.Stride);
        }

        using var imported = FF.Frame.FromDrmPrime(
            new FF.DrmPrimeImage([.. objects], [new FF.DrmLayer(image.DrmFormat, [.. planes])]),
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

    public override void Prepare(in VideoFrame frame, FF.Frame destination)
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
