#if MACOS_HEAD
using System.Runtime.Versioning;
using CoreVideo;
using Foundation;
using IOSurface;
using Metal;
using ObjCRuntime;

namespace StreamTransport.Agent;

/// <summary>
/// Shared Metal device + the app's precompiled shader library (default.metallib, built from Shaders/*.metal by
/// the macOS SDK's Metal toolchain). One per process; the compute codecs build their pipelines from it. The
/// macOS analog of the Linux Vulkan compute context - reached entirely through the Microsoft Metal bindings,
/// so there is no native shim (the former Syphon.NET SurfaceEffect role).
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MetalContext
{
    private static readonly Lock Gate = new();
    private static IMTLDevice? _device;
    private static IMTLCommandQueue? _queue;
    private static IMTLLibrary? _library;

    public static IMTLDevice Device => Ensure().Device;
    public static IMTLCommandQueue Queue => Ensure().Queue;
    public static IMTLLibrary Library => Ensure().Library;

    private static (IMTLDevice Device, IMTLCommandQueue Queue, IMTLLibrary Library) Ensure()
    {
        lock (Gate)
        {
            if (_device is null)
            {
                _device = MTLDevice.SystemDefault ?? throw new InvalidOperationException("No Metal device available.");
                _queue = _device.CreateCommandQueue() ?? throw new InvalidOperationException("Failed to create a Metal command queue.");
                _library = _device.CreateDefaultLibrary() ?? throw new InvalidOperationException("default.metallib not found in the app bundle.");
            }

            return (_device, _queue!, _library!);
        }
    }
}

/// <summary>
/// Runs one Metal compute kernel (by function name in <see cref="MetalContext.Library"/>) over input IOSurfaces
/// to produce a BGRA output IOSurface - the SurfaceEffect replacement, built directly on the Microsoft Metal +
/// IOSurface bindings. Inputs are wrapped zero-copy as MTLTextures from their IOSurface planes; the output is a
/// reused IOSurface-backed texture (recreated only on size change), so the result is valid until the next call
/// of the same instance - exactly the lifetime contract the codecs relied on. macOS-only.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MetalSurfaceCompute : IDisposable
{
    /// <summary>An input plane: the source IOSurface handle, the Metal format to view it as, the plane index, and its dimensions.</summary>
    public readonly record struct Input(nint Surface, MTLPixelFormat Format, int Plane, int Width, int Height);

    private readonly IMTLComputePipelineState _pipeline;
    private IOSurface.IOSurface? _output;
    private IMTLTexture? _outputTexture;
    private int _outWidth;
    private int _outHeight;
    private bool _disposed;

    public MetalSurfaceCompute(string functionName)
    {
        using IMTLFunction fn = MetalContext.Library.CreateFunction(functionName)
            ?? throw new InvalidOperationException($"Metal function '{functionName}' not found in default.metallib.");
        _pipeline = MetalContext.Device.CreateComputePipelineState(fn, out NSError? error)
            ?? throw new InvalidOperationException($"Failed to create compute pipeline '{functionName}': {error?.LocalizedDescription}");
    }

    /// <summary>Run the kernel; returns the BGRA output IOSurface handle (valid until the next Run on this instance).</summary>
    public nint Run(int outWidth, int outHeight, ReadOnlySpan<Input> inputs)
    {
        EnsureOutput(outWidth, outHeight);

        // Wrap each input IOSurface plane as an MTLTexture (zero-copy), bound at textures 0..n-1; output at n.
        var inputTextures = new IMTLTexture[inputs.Length];
        for (int i = 0; i < inputs.Length; i++)
        {
            inputTextures[i] = CreateTexture(inputs[i].Surface, inputs[i].Format, inputs[i].Plane,
                inputs[i].Width, inputs[i].Height, MTLTextureUsage.ShaderRead);
        }

        IMTLCommandBuffer cb = MetalContext.Queue.CommandBuffer()!;
        IMTLComputeCommandEncoder enc = cb.ComputeCommandEncoder!;
        enc.SetComputePipelineState(_pipeline);
        for (int i = 0; i < inputTextures.Length; i++)
        {
            enc.SetTexture(inputTextures[i], (nuint)i);
        }

        enc.SetTexture(_outputTexture!, (nuint)inputTextures.Length);

        var threadgroup = new MTLSize(16, 16, 1);
        var groups = new MTLSize((outWidth + 15) / 16, (outHeight + 15) / 16, 1);
        enc.DispatchThreadgroups(groups, threadgroup);
        enc.EndEncoding();
        cb.Commit();
        cb.WaitUntilCompleted();

        foreach (IMTLTexture t in inputTextures)
        {
            t.Dispose();
        }

        return _output!.Handle.Handle;
    }

    private void EnsureOutput(int width, int height)
    {
        if (_output is not null && _outWidth == width && _outHeight == height)
        {
            return;
        }

        _outputTexture?.Dispose();
        _output?.Dispose();
        _output = new IOSurface.IOSurface(new IOSurfaceOptions
        {
            Width = width,
            Height = height,
            BytesPerElement = 4,
            PixelFormat = (int)CVPixelFormatType.CV32BGRA,
        });
        _outputTexture = CreateTexture(_output.Handle.Handle, MTLPixelFormat.BGRA8Unorm, 0, width, height,
            MTLTextureUsage.ShaderWrite | MTLTextureUsage.ShaderRead);
        _outWidth = width;
        _outHeight = height;
    }

    private static IMTLTexture CreateTexture(nint surfaceHandle, MTLPixelFormat format, int plane, int width, int height, MTLTextureUsage usage)
    {
        var surface = Runtime.GetINativeObject<IOSurface.IOSurface>(surfaceHandle, owns: false)
            ?? throw new InvalidOperationException("Could not wrap the IOSurface handle.");
        var desc = MTLTextureDescriptor.CreateTexture2DDescriptor(format, (nuint)width, (nuint)height, mipmapped: false);
        desc.Usage = usage;
        return MetalContext.Device.CreateTexture(desc, surface, (nuint)plane)
            ?? throw new InvalidOperationException("CreateTexture from IOSurface returned null.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _outputTexture?.Dispose();
        _output?.Dispose();
        _pipeline.Dispose();
    }
}
#endif
