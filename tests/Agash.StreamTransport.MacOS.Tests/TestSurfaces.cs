using System.Runtime.InteropServices;
using Agash.StreamTransport.MacOS.Metal;
using Agash.StreamTransport.Media;
using IOSurface;
using ObjCRuntime;

namespace Agash.StreamTransport.MacOS.Tests;

// IOSurfaces written and read on the CPU, row by row at each plane's stride.
internal static class TestSurfaces
{
    public static GpuIdentity Device { get; } = MetalEngine.For(null).Identity;

    // A pooled surface holding the planes, tightly packed rows each; the pool goes with the surface.
    public static PooledSurface Upload(
        PixelFormat format,
        int width,
        int height,
        params byte[][] planes
    )
    {
        using IOSurfacePool pool = new(format, new VideoSize(width, height));
        PooledSurface surface = pool.Rent();
        Write(surface.Surface, format, width, height, planes);
        return surface;
    }

    public static byte[][] Download(IOSurfaceImage image, PixelFormat format, int width, int height)
    {
        IOSurface.IOSurface surface = Runtime.GetINativeObject<IOSurface.IOSurface>(
            image.Surface,
            false
        )!;
        int count = format == PixelFormat.Nv12 ? 2 : 1;
        byte[][] planes = new byte[count][];
        _ = surface.Lock(IOSurfaceLockOptions.ReadOnly);
        try
        {
            for (int plane = 0; plane < count; plane++)
            {
                (int rowBytes, int rows) = Plane(format, plane, width, height);
                planes[plane] = new byte[rowBytes * rows];
                (nint address, int stride) = Address(surface, format, plane);
                for (int row = 0; row < rows; row++)
                {
                    Marshal.Copy(address + (row * stride), planes[plane], row * rowBytes, rowBytes);
                }
            }
        }
        finally
        {
            _ = surface.Unlock(IOSurfaceLockOptions.ReadOnly);
        }

        return planes;
    }

    public static VideoFrame Frame(
        PooledSurface surface,
        PixelFormat format,
        int width,
        int height,
        VideoColor color = default
    ) =>
        new(
            new VideoStorage(new IOSurfaceImage(surface.Handle, Device)),
            new VideoFormat(format, width, height),
            MediaTimestamp.Captured(new MediaTime(42)),
            color: color,
            retainer: new Held(surface)
        );

    private static void Write(
        IOSurface.IOSurface surface,
        PixelFormat format,
        int width,
        int height,
        byte[][] planes
    )
    {
        _ = surface.Lock(default(IOSurfaceLockOptions));
        try
        {
            for (int plane = 0; plane < planes.Length; plane++)
            {
                (int rowBytes, int rows) = Plane(format, plane, width, height);
                (nint address, int stride) = Address(surface, format, plane);
                for (int row = 0; row < rows; row++)
                {
                    Marshal.Copy(planes[plane], row * rowBytes, address + (row * stride), rowBytes);
                }
            }
        }
        finally
        {
            _ = surface.Unlock(default(IOSurfaceLockOptions));
        }
    }

    private static (int RowBytes, int Rows) Plane(
        PixelFormat format,
        int plane,
        int width,
        int height
    ) =>
        format == PixelFormat.Nv12
            ? (plane == 0 ? (width, height) : (width, height / 2))
            : (width * 4, height);

    private static (nint Address, int Stride) Address(
        IOSurface.IOSurface surface,
        PixelFormat format,
        int plane
    ) =>
        format == PixelFormat.Nv12
            ? (surface.GetBaseAddress((nuint)plane), (int)surface.GetBytesPerRow((nuint)plane))
            : (surface.BaseAddress, (int)surface.BytesPerRow);

    // A test surface kept by holding it again.
    private sealed class Held(PooledSurface surface) : IVideoFrameRetainer
    {
        public VideoFrameLease Retain(in VideoFrame frame)
        {
            surface.Hold();
            return new IOSurfaceFrameLease(in frame, surface);
        }
    }
}
