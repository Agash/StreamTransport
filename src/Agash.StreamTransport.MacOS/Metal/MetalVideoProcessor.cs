using System.Numerics;
using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Foundation;
using IOSurface;
using Metal;
using ObjCRuntime;

namespace Agash.StreamTransport.MacOS.Metal;

/// <summary>
/// Makes Metal compute processors on IOSurfaces: BGRA to NV12 at any size, packing alpha
/// side by side when asked, and NV12 to BGRA, unpacking side-by-side alpha when asked. Frames stay on
/// the GPU they arrive on, and leave finished: VideoToolbox and Syphon read IOSurfaces directly.
/// </summary>
public sealed class MetalVideoProcessorFactory : IVideoProcessorFactory
{
    private const string Name = "Metal compute";

    /// <inheritdoc/>
    public int Rank => 100;

    /// <inheritdoc/>
    public VideoProcessorInfo? QueryCapabilities(
        VideoStreamDescription input,
        VideoProcessing processing
    )
    {
        ArgumentNullException.ThrowIfNull(processing);
        VideoConstraints output = processing.Output;

        // Frames in memory for a consumer on the GPU are uploaded into an IOSurface, converted there when
        // the consumer takes another format: a camera or a test signal feeding Syphon or VideoToolbox.
        if (input.Storage == VideoStorageKind.Cpu)
        {
            return Upload(input, processing);
        }

        // The work runs on the input's GPU; the result stays there as an IOSurface, or is read back for
        // a consumer in memory (a software encoder fed by a GPU source).
        VideoStorageKind? result =
            output.Storages.Contains(VideoStorageKind.IOSurface) ? VideoStorageKind.IOSurface
            : output.Storages.Contains(VideoStorageKind.Cpu) ? VideoStorageKind.Cpu
            : null;
        if (
            input.Storage != VideoStorageKind.IOSurface
            || result is not { } storage
            || (
                storage == VideoStorageKind.IOSurface
                && output.Device is { } wanted
                && input.Device is { } actual
                && wanted != actual
            )
        )
        {
            return null;
        }

        if (input.PixelFormat == PixelFormat.Bgra)
        {
            if (
                processing.Alpha == AlphaLayout.UnpackSideBySide
                || !output.PixelFormats.Contains(PixelFormat.Nv12)
            )
            {
                return null;
            }

            VideoSize colour = processing.Size ?? input.Size;
            VideoSize size =
                processing.Alpha == AlphaLayout.PackSideBySide
                    ? colour with
                    {
                        Width = colour.Width * 2,
                    }
                    : colour;
            return size.Width % 2 == 0 && size.Height % 2 == 0
                ? Info(PixelFormat.Nv12, size, input.Device, storage)
                : null;
        }

        if (input.PixelFormat == PixelFormat.Nv12)
        {
            VideoSize colour =
                processing.Alpha == AlphaLayout.UnpackSideBySide
                    ? input.Size with
                    {
                        Width = input.Size.Width / 2,
                    }
                    : input.Size;
            return
                processing.Alpha == AlphaLayout.PackSideBySide
                || (processing.Size is { } s && s != colour)
                || !output.PixelFormats.Contains(PixelFormat.Bgra)
                ? null
                : Info(PixelFormat.Bgra, colour, input.Device, storage);
        }

        return null;
    }

    private static VideoProcessorInfo? Upload(
        VideoStreamDescription input,
        VideoProcessing processing
    )
    {
        VideoConstraints output = processing.Output;
        if (
            !output.Storages.Contains(VideoStorageKind.IOSurface)
            || input.PixelFormat is not (PixelFormat.Nv12 or PixelFormat.Bgra)
            || processing.Alpha != AlphaLayout.None
            || (processing.Size is { } size && size != input.Size)
            || input.Size.Width % 2 != 0
            || input.Size.Height % 2 != 0
        )
        {
            return null;
        }

        PixelFormat? target =
            output.PixelFormats.Contains(input.PixelFormat) ? input.PixelFormat
            : input.PixelFormat == PixelFormat.Nv12
            && output.PixelFormats.Contains(PixelFormat.Bgra)
                ? PixelFormat.Bgra
            : input.PixelFormat == PixelFormat.Bgra
            && output.PixelFormats.Contains(PixelFormat.Nv12)
                ? PixelFormat.Nv12
            : null;
        return target is { } format
            ? Info(format, input.Size, output.Device, VideoStorageKind.IOSurface)
            : null;
    }

    /// <inheritdoc/>
    public IVideoProcessor Create(VideoStreamDescription input, VideoProcessing processing)
    {
        VideoProcessorInfo info =
            QueryCapabilities(input, processing)
            ?? throw new ArgumentException(
                $"{Name} cannot turn {input} into what was asked.",
                nameof(processing)
            );
        return new MetalVideoProcessor(
            info,
            processing,
            input.PixelFormat,
            MetalEngine.For(input.Device ?? processing.Output.Device)
        );
    }

    private static VideoProcessorInfo Info(
        PixelFormat format,
        VideoSize size,
        GpuIdentity? device,
        VideoStorageKind storage
    ) =>
        new(
            Name,
            IsHardwareAccelerated: true,
            new VideoStreamDescription(
                storage,
                format,
                size,
                storage == VideoStorageKind.IOSurface ? device : null
            )
        );
}

/// <summary>One stream's conversions on the GPU of its frames.</summary>
internal sealed unsafe class MetalVideoProcessor : IVideoProcessor, IVideoFrameRetainer
{
    private readonly VideoProcessing _processing;
    private readonly MetalEngine _engine;
    private readonly IOSurfacePool _pool;
    private readonly IOSurfacePool _staging;
    private readonly VideoColor _outputColour;
    private readonly Lock _gate = new();
    private PooledSurface? _delivering;
    private bool _disposed;

    public MetalVideoProcessor(
        VideoProcessorInfo info,
        VideoProcessing processing,
        PixelFormat inputFormat,
        MetalEngine engine
    )
    {
        Info = info with { Output = info.Output with { Device = engine.Identity } };
        _processing = processing;
        _engine = engine;
        _outputColour =
            info.Output.PixelFormat == PixelFormat.Nv12
                ? processing.Color ?? VideoColor.Bt709
                : VideoColor.Srgb;
        _pool = new IOSurfacePool(info.Output.PixelFormat, info.Output.Size, _outputColour.Range);
        _staging = new IOSurfacePool(inputFormat, info.Output.Size);
    }

    public VideoProcessorInfo Info { get; }

    public void Process(in VideoFrame frame, IVideoFrameConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        PooledSurface? staged = null;
        IOSurfaceImage image;
        if (frame.Storage.Kind == VideoStorageKind.Cpu)
        {
            staged = Stage(in frame);
            image = new IOSurfaceImage(staged.Handle, _engine.Identity);
        }
        else if (!frame.Storage.TryGetValue(out image))
        {
            throw new ArgumentException("The frame is not an IOSurface.", nameof(frame));
        }

        if (staged is not null && frame.Format.PixelFormat == Info.Output.PixelFormat)
        {
            // An upload alone: the staged surface is the result.
            Deliver(staged, in frame, frame.Color, consumer);
            return;
        }

        try
        {
            ProcessSurface(in frame, image, consumer);
        }
        finally
        {
            staged?.Release();
        }
    }

    // Copies a frame in memory into a surface of the staging pool, plane by plane.
    private unsafe PooledSurface Stage(in VideoFrame frame)
    {
        PooledSurface staged;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            staged = _staging.Rent();
        }

        IOSurface.IOSurface surface = staged.Surface;
        _ = frame.Storage.TryGetValue(out CpuImage cpu);
        _ = surface.Lock(0);
        try
        {
            int height = frame.Format.CodedSize.Height;
            for (int plane = 0; plane < frame.PlaneCount; plane++)
            {
                bool planar = frame.Format.PixelFormat == PixelFormat.Nv12;
                byte* target = (byte*)(
                    planar ? surface.GetBaseAddress((nuint)plane) : surface.BaseAddress
                );
                int targetStride = (int)(
                    planar ? surface.GetBytesPerRow((nuint)plane) : surface.BytesPerRow
                );
                int rows = PlaneLayout.PlaneRows(frame.Format.PixelFormat, plane, height);
                int rowBytes = PlaneLayout
                    .Packed(frame.Format.PixelFormat, frame.Format.CodedSize)[plane]
                    .Stride;
                int stride = cpu.Planes[plane].Stride;
                ReadOnlySpan<byte> source = frame.GetPlane(plane);
                for (int y = 0; y < rows; y++)
                {
                    source
                        .Slice(y * stride, rowBytes)
                        .CopyTo(new Span<byte>(target + (y * targetStride), rowBytes));
                }
            }
        }
        finally
        {
            _ = surface.Unlock(0);
        }

        return staged;
    }

    // Hands on a finished surface on the GPU, which the consumer may keep.
    private void Deliver(
        PooledSurface surface,
        in VideoFrame frame,
        VideoColor color,
        IVideoFrameConsumer consumer
    )
    {
        VideoStreamDescription described = Info.Output;
        VideoFrame result = new(
            new VideoStorage(new IOSurfaceImage(surface.Handle, _engine.Identity)),
            new VideoFormat(described.PixelFormat, described.Size.Width, described.Size.Height),
            frame.Timestamp,
            color: color,
            orientation: frame.Orientation,
            duration: frame.Duration,
            retainer: this
        );
        lock (_gate)
        {
            _delivering = surface;
            try
            {
                consumer.OnFrame(in result);
            }
            finally
            {
                _delivering = null;
                surface.Release();
            }
        }
    }

    private void ProcessSurface(
        in VideoFrame frame,
        IOSurfaceImage image,
        IVideoFrameConsumer consumer
    )
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Metal hands back autoreleased objects; frames arrive on threads AppKit does not drain.
            using NSAutoreleasePool autoreleased = new();
            PooledSurface output = _pool.Rent();
            try
            {
                IOSurface.IOSurface input = Runtime.GetINativeObject<IOSurface.IOSurface>(
                    image.Surface,
                    false
                )!;
                using IMTLCommandBuffer commands = _engine.Begin(
                    image.SharedEvent,
                    image.SignalValue
                );
                if (Info.Output.PixelFormat == PixelFormat.Nv12)
                {
                    ToYuv(commands, input, output);
                }
                else
                {
                    ToRgb(commands, input, frame.Color, output);
                }

                // The GPU is done with the input before this returns, so the producer may reuse it.
                MetalEngine.Complete(commands);
            }
            catch
            {
                output.Release();
                throw;
            }

            VideoStreamDescription described = Info.Output;
            if (described.Storage == VideoStorageKind.Cpu)
            {
                try
                {
                    ReadBack(output, in frame, consumer);
                }
                finally
                {
                    output.Release();
                }

                return;
            }

            VideoFrame result = new(
                new VideoStorage(new IOSurfaceImage(output.Handle, _engine.Identity)),
                new VideoFormat(described.PixelFormat, described.Size.Width, described.Size.Height),
                frame.Timestamp,
                color: _outputColour,
                orientation: frame.Orientation,
                duration: frame.Duration,
                retainer: this
            );
            _delivering = output;
            try
            {
                consumer.OnFrame(in result);
            }
            finally
            {
                _delivering = null;
                output.Release();
            }
        }
    }

    // The finished surface read in place under a read-only lock and handed on from memory; a consumer
    // that keeps it copies it.
    private unsafe void ReadBack(
        PooledSurface output,
        in VideoFrame frame,
        IVideoFrameConsumer consumer
    )
    {
        VideoStreamDescription described = Info.Output;
        IOSurface.IOSurface surface = output.Surface;
        bool nv12 = described.PixelFormat == PixelFormat.Nv12;
        int height = described.Size.Height;
        _ = surface.Lock(IOSurfaceLockOptions.ReadOnly);
        try
        {
            (nint first, int firstStride) = nv12
                ? (surface.GetBaseAddress(0), (int)surface.GetBytesPerRow(0))
                : (surface.BaseAddress, (int)surface.BytesPerRow);
            (nint second, int secondStride) = nv12
                ? (surface.GetBaseAddress(1), (int)surface.GetBytesPerRow(1))
                : (0, 0);
            VideoFrame result = new(
                new VideoFormat(described.PixelFormat, described.Size.Width, height),
                frame.Timestamp,
                new ReadOnlySpan<byte>((void*)first, firstStride * height),
                firstStride,
                nv12 ? new ReadOnlySpan<byte>((void*)second, secondStride * (height / 2)) : default,
                secondStride,
                color: _outputColour,
                orientation: frame.Orientation,
                duration: frame.Duration
            );
            consumer.OnFrame(in result);
        }
        finally
        {
            _ = surface.Unlock(IOSurfaceLockOptions.ReadOnly);
        }
    }

    // Called by a consumer inside OnFrame, on the processing thread that holds the lock.
    public VideoFrameLease Retain(in VideoFrame frame)
    {
        PooledSurface surface =
            _delivering
            ?? throw new InvalidOperationException("Frames are retained only while delivered.");
        surface.Hold();
        return new IOSurfaceFrameLease(in frame, surface);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                _pool.Dispose();
                _staging.Dispose();
            }
        }
    }

    // BGRA to NV12, packing alpha to the right when the output is twice the colour width.
    private void ToYuv(IMTLCommandBuffer commands, IOSurface.IOSurface input, PooledSurface output)
    {
        (Vector4 y, Vector4 cb, Vector4 cr) = ColorConversion.RgbToYCbCr(_outputColour);
        VideoSize size = Info.Output.Size;
        bool pack = _processing.Alpha == AlphaLayout.PackSideBySide;
        RgbToYuvParameters parameters = new()
        {
            OutWidth = (uint)size.Width,
            OutHeight = (uint)size.Height,
            ColourWidth = (uint)(pack ? size.Width / 2 : size.Width),
            Pack = pack ? 1u : 0u,
            RowY = y,
            RowCb = cb,
            RowCr = cr,
        };

        using IMTLTexture source = _engine.Texture(
            input,
            0,
            MTLPixelFormat.BGRA8Unorm,
            (int)input.Width,
            (int)input.Height,
            MTLTextureUsage.ShaderRead
        );
        using IMTLTexture luma = _engine.Texture(
            output.Surface,
            0,
            MTLPixelFormat.R8Unorm,
            size.Width,
            size.Height,
            MTLTextureUsage.ShaderWrite
        );
        using IMTLTexture chroma = _engine.Texture(
            output.Surface,
            1,
            MTLPixelFormat.RG8Unorm,
            size.Width / 2,
            size.Height / 2,
            MTLTextureUsage.ShaderWrite
        );

        using IMTLComputeCommandEncoder encoder = Encoder(commands, _engine.RgbToYuv);
        encoder.SetTexture(source, 0);
        encoder.SetTexture(luma, 1);
        encoder.SetTexture(chroma, 2);
        encoder.SetBytes((nint)(&parameters), (nuint)sizeof(RgbToYuvParameters), 0);
        encoder.DispatchThreads(
            new MTLSize(size.Width / 2, size.Height / 2, 1),
            new MTLSize(8, 8, 1)
        );
        encoder.EndEncoding();
    }

    // NV12 to BGRA, taking alpha from the right half when the input carries it side by side.
    private void ToRgb(
        IMTLCommandBuffer commands,
        IOSurface.IOSurface input,
        VideoColor inputColour,
        PooledSurface output
    )
    {
        VideoColor source =
            inputColour.Matrix == ColorMatrix.Unspecified
                ? VideoColor.Bt709 with
                {
                    Range = inputColour.Range,
                }
                : inputColour;
        (float offset, float scale, Vector4 r, Vector4 g, Vector4 b) = ColorConversion.YCbCrToRgb(
            source
        );
        VideoSize size = Info.Output.Size;
        YuvToRgbParameters parameters = new()
        {
            OutWidth = (uint)size.Width,
            OutHeight = (uint)size.Height,
            Unpack = _processing.Alpha == AlphaLayout.UnpackSideBySide ? 1u : 0u,
            LumaScale = new Vector4(offset, scale, ColorConversion.ChromaCentre, 0),
            RowR = r,
            RowG = g,
            RowB = b,
        };

        int width = (int)input.Width;
        int height = (int)input.Height;
        using IMTLTexture luma = _engine.Texture(
            input,
            0,
            MTLPixelFormat.R8Unorm,
            width,
            height,
            MTLTextureUsage.ShaderRead
        );
        using IMTLTexture chroma = _engine.Texture(
            input,
            1,
            MTLPixelFormat.RG8Unorm,
            (width + 1) / 2,
            (height + 1) / 2,
            MTLTextureUsage.ShaderRead
        );
        using IMTLTexture rgb = _engine.Texture(
            output.Surface,
            0,
            MTLPixelFormat.BGRA8Unorm,
            size.Width,
            size.Height,
            MTLTextureUsage.ShaderWrite
        );

        using IMTLComputeCommandEncoder encoder = Encoder(commands, _engine.YuvToRgb);
        encoder.SetTexture(luma, 0);
        encoder.SetTexture(chroma, 1);
        encoder.SetTexture(rgb, 2);
        encoder.SetBytes((nint)(&parameters), (nuint)sizeof(YuvToRgbParameters), 0);
        encoder.DispatchThreads(new MTLSize(size.Width, size.Height, 1), new MTLSize(8, 8, 1));
        encoder.EndEncoding();
    }

    private static IMTLComputeCommandEncoder Encoder(
        IMTLCommandBuffer commands,
        IMTLComputePipelineState pipeline
    )
    {
        IMTLComputeCommandEncoder encoder =
            commands.ComputeCommandEncoder
            ?? throw new InvalidOperationException("The command buffer gave no compute encoder.");
        encoder.SetComputePipelineState(pipeline);
        return encoder;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RgbToYuvParameters
    {
        public uint OutWidth;
        public uint OutHeight;
        public uint ColourWidth;
        public uint Pack;
        public Vector4 RowY;
        public Vector4 RowCb;
        public Vector4 RowCr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct YuvToRgbParameters
    {
        public uint OutWidth;
        public uint OutHeight;
        public uint Unpack;
        public uint Padding;
        public Vector4 LumaScale;
        public Vector4 RowR;
        public Vector4 RowG;
        public Vector4 RowB;
    }
}
