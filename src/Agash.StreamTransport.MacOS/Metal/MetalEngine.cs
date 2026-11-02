using System.Collections.Concurrent;
using Agash.StreamTransport.Media;
using Foundation;
using Metal;
using ObjCRuntime;

namespace Agash.StreamTransport.MacOS.Metal;

/// <summary>
/// A Metal device with the queue and compute pipelines the package's processors, sources and sinks run
/// on. There is one per device for the life of the process: Metal devices are, and the kernels are
/// compiled once for each. Metal command queues take work from any thread.
/// </summary>
internal sealed class MetalEngine
{
    private static readonly ConcurrentDictionary<ulong, Lazy<MetalEngine>> Engines = new();

    private MetalEngine(IMTLDevice device)
    {
        Device = device;
        Identity = GpuIdentity.FromMetalRegistryId(device.RegistryId);
        Queue =
            device.CreateCommandQueue()
            ?? throw new InvalidOperationException($"{device.Name} gave no command queue.");
        IMTLLibrary library = Compile(device);
        RgbToYuv = Pipeline(device, library, "rgb_to_yuv");
        YuvToRgb = Pipeline(device, library, "yuv_to_rgb");
    }

    public IMTLDevice Device { get; }

    public GpuIdentity Identity { get; }

    public IMTLCommandQueue Queue { get; }

    public IMTLComputePipelineState RgbToYuv { get; }

    public IMTLComputePipelineState YuvToRgb { get; }

    /// <summary>The engine of a device: the one a GPU identity names, or the system's default GPU.</summary>
    /// <param name="device">A Metal registry id; null for the default GPU.</param>
    /// <returns>The engine.</returns>
    /// <exception cref="ArgumentException">The identity is not a Metal device of this system.</exception>
    public static MetalEngine For(GpuIdentity? device)
    {
        IMTLDevice metal = Find(device);
        return Engines
            .GetOrAdd(metal.RegistryId, _ => new Lazy<MetalEngine>(() => new MetalEngine(metal)))
            .Value;
    }

    /// <summary>A command buffer, waiting first for a producer's shared event when it has one.</summary>
    /// <param name="sync">The event's handle, or zero.</param>
    /// <param name="value">The value it reaches when the producer is done.</param>
    /// <returns>The command buffer.</returns>
    public IMTLCommandBuffer Begin(nint sync = 0, ulong value = 0)
    {
        IMTLCommandBuffer commands =
            Queue.CommandBuffer()
            ?? throw new InvalidOperationException("The command queue gave no command buffer.");
        if (sync != 0)
        {
            commands.EncodeWait(Runtime.GetINativeObject<IMTLSharedEvent>(sync, false)!, value);
        }

        return commands;
    }

    /// <summary>
    /// Commits a command buffer and waits until the GPU has run it. Frames leave the package finished:
    /// VideoToolbox and Syphon clients read IOSurfaces directly and cannot wait on a Metal event.
    /// </summary>
    /// <param name="commands">The command buffer.</param>
    /// <exception cref="InvalidOperationException">The GPU failed to run it.</exception>
    public static void Complete(IMTLCommandBuffer commands)
    {
        commands.Commit();
        commands.WaitUntilCompleted();
        if (commands.Status == MTLCommandBufferStatus.Error)
        {
            throw new InvalidOperationException(
                $"The GPU failed to run the commands: {commands.Error?.LocalizedDescription}"
            );
        }
    }

    /// <summary>A texture over one plane of an IOSurface.</summary>
    /// <param name="surface">The surface.</param>
    /// <param name="plane">The plane.</param>
    /// <param name="format">How the shaders see the plane's texels.</param>
    /// <param name="width">The plane's width.</param>
    /// <param name="height">The plane's height.</param>
    /// <param name="usage">What the shaders do with it.</param>
    /// <returns>The texture; dispose it after the work that uses it is committed.</returns>
    public IMTLTexture Texture(
        IOSurface.IOSurface surface,
        int plane,
        MTLPixelFormat format,
        int width,
        int height,
        MTLTextureUsage usage
    )
    {
        using var descriptor = MTLTextureDescriptor.CreateTexture2DDescriptor(
            format,
            (nuint)width,
            (nuint)height,
            mipmapped: false
        );
        descriptor.Usage = usage;
        descriptor.StorageMode = MTLStorageMode.Shared;
        return Device.CreateTexture(descriptor, surface, (nuint)plane)
            ?? throw new InvalidOperationException(
                $"Plane {plane} of the IOSurface cannot back a {format} texture."
            );
    }

    private static IMTLDevice Find(GpuIdentity? device)
    {
        if (device is not { } wanted)
        {
            return MTLDevice.SystemDefault
                ?? throw new InvalidOperationException("This system has no Metal device.");
        }

        if (wanted.Kind != GpuIdentityKind.MetalRegistryId)
        {
            throw new ArgumentException($"{wanted} is not a Metal device.", nameof(device));
        }

        foreach (IMTLDevice candidate in MTLDevice.GetAllDevices() ?? [])
        {
            if (candidate.RegistryId == wanted.Value)
            {
                return candidate;
            }
        }

        throw new ArgumentException(
            $"No Metal device has registry id {wanted.Value}.",
            nameof(device)
        );
    }

    private static IMTLLibrary Compile(IMTLDevice device)
    {
        using Stream source =
            typeof(MetalEngine).Assembly.GetManifestResourceStream("VideoKernels.metal")
            ?? throw new InvalidOperationException("The Metal kernels are not in the assembly.");
        using StreamReader reader = new(source);
        using MTLCompileOptions options = new();
        return device.CreateLibrary(reader.ReadToEnd(), options, out NSError? error)
            ?? throw new InvalidOperationException(
                $"The Metal kernels did not compile: {error?.LocalizedDescription}"
            );
    }

    private static IMTLComputePipelineState Pipeline(
        IMTLDevice device,
        IMTLLibrary library,
        string name
    )
    {
        using IMTLFunction function =
            library.CreateFunction(name)
            ?? throw new InvalidOperationException($"The Metal kernels have no {name}.");
        return device.CreateComputePipelineState(function, out NSError? error)
            ?? throw new InvalidOperationException(
                $"The {name} pipeline failed: {error?.LocalizedDescription}"
            );
    }
}
