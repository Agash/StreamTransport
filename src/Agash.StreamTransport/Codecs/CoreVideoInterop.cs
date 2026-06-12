using System.Runtime.InteropServices;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// Minimal CoreVideo / CoreFoundation interop for the macOS zero-copy path: wraps a Syphon IOSurface as
/// a <c>CVPixelBuffer</c> so it can be handed to VideoToolbox without a CPU copy, and manages the
/// CoreFoundation reference counts. macOS-only; the P/Invoke declarations compile on every platform but
/// are only called on macOS.
/// </summary>
internal static partial class CoreVideoInterop
{
    private const string CoreVideo = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>Create a CVPixelBuffer backed by the given IOSurface (zero-copy). Returns the CVPixelBufferRef.</summary>
    public static nint CreatePixelBufferFromIOSurface(nint ioSurface)
    {
        int result = CVPixelBufferCreateWithIOSurface(0, ioSurface, 0, out nint pixelBuffer);
        if (result != 0 || pixelBuffer == 0)
        {
            throw new InvalidOperationException($"CVPixelBufferCreateWithIOSurface failed ({result}).");
        }

        return pixelBuffer;
    }

    /// <summary>Get the IOSurface backing a CVPixelBuffer (for the publish side). May be 0.</summary>
    public static nint GetIOSurface(nint pixelBuffer) => CVPixelBufferGetIOSurface(pixelBuffer);

    /// <summary>Release a CoreFoundation object (CVPixelBuffer, etc.).</summary>
    public static void Release(nint cfObject)
    {
        if (cfObject != 0)
        {
            CFRelease(cfObject);
        }
    }

    [LibraryImport(CoreVideo)]
    private static partial int CVPixelBufferCreateWithIOSurface(nint allocator, nint surface, nint attributes, out nint pixelBufferOut);

    [LibraryImport(CoreVideo)]
    private static partial nint CVPixelBufferGetIOSurface(nint pixelBuffer);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint cf);
}
