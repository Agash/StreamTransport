using System.Numerics;
using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Vortice.Vulkan;

namespace Agash.StreamTransport.Linux.Vulkan;

/// <summary>
/// Makes Vulkan compute processors on DMA-BUFs: BGRA or RGBA to NV12 at any size, packing alpha side
/// by side when asked, and NV12 to BGRA or RGBA, unpacking side-by-side alpha when asked. Frames stay
/// on the GPU they arrive on and leave finished, on DMA-BUFs in rows that VAAPI encoders, PipeWire
/// consumers and compositors import as they are.
/// </summary>
public sealed class VulkanVideoProcessorFactory : IVideoProcessorFactory
{
    private const string Name = "Vulkan compute";

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

        // The work runs on the input's GPU; the result stays there as a DMA-BUF, or is read back for a
        // consumer in memory (a software encoder fed by a GPU source).
        VideoStorageKind? result =
            output.Storages.Contains(VideoStorageKind.DmaBuf) ? VideoStorageKind.DmaBuf
            : output.Storages.Contains(VideoStorageKind.Cpu) ? VideoStorageKind.Cpu
            : null;
        if (
            input.Storage != VideoStorageKind.DmaBuf
            || result is not { } storage
            || (
                storage == VideoStorageKind.DmaBuf
                && (
                    (output.Device is { } wanted && input.Device is { } actual && wanted != actual)
                    || (
                        !output.DrmModifiers.IsDefaultOrEmpty
                        && !output.DrmModifiers.Contains(VulkanEngine.LinearModifier)
                    )
                )
            )
        )
        {
            return null;
        }

        if (input.PixelFormat is PixelFormat.Bgra or PixelFormat.Rgba)
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
            // The colour itself must be even: 4:2:0 chroma averages 2x2 blocks, and an odd colour width
            // would put a block across the colour and alpha halves of a packed frame.
            return colour.Width % 2 == 0 && colour.Height % 2 == 0
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
            if (
                processing.Alpha == AlphaLayout.PackSideBySide
                || (processing.Size is { } s && s != colour)
            )
            {
                return null;
            }

            foreach (PixelFormat format in output.PixelFormats)
            {
                if (format is PixelFormat.Bgra or PixelFormat.Rgba)
                {
                    return Info(format, colour, input.Device, storage);
                }
            }
        }

        return null;
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
        return new VulkanVideoProcessor(info, processing, VulkanEngine.For(input.Device));
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
                storage == VideoStorageKind.DmaBuf ? device : null
            )
        );
}

/// <summary>One stream's conversions on the GPU of its frames.</summary>
internal sealed unsafe class VulkanVideoProcessor : IVideoProcessor, IVideoFrameRetainer
{
    // How long a producer's GPU may still be writing a frame when it hands it over.
    private readonly VideoProcessing _processing;
    private readonly VulkanEngine _engine;
    private readonly DmaBufPool _pool;
    private readonly VideoColor _outputColour;
    private readonly Lock _gate = new();
    private PooledDmaBuf? _delivering;
    private ulong _submitted;
    private bool _disposed;

    public VulkanVideoProcessor(
        VideoProcessorInfo info,
        VideoProcessing processing,
        VulkanEngine engine
    )
    {
        Info = info with { Output = info.Output with { Device = engine.Identity } };
        _processing = processing;
        _engine = engine;
        _outputColour =
            info.Output.PixelFormat == PixelFormat.Nv12
                ? processing.Color ?? VideoColor.Bt709
                : VideoColor.Srgb;
        _pool = new DmaBufPool(engine, info.Output.PixelFormat, info.Output.Size);
    }

    public VideoProcessorInfo Info { get; }

    public void Process(in VideoFrame frame, IVideoFrameConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        if (!frame.Storage.TryGetValue(out DmaBufImage image))
        {
            throw new ArgumentException("The frame is not a DMA-BUF.", nameof(frame));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PooledDmaBuf output = _pool.Rent();
            ulong ready;
            try
            {
                ready = Convert(in frame, image, output);
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
                    _engine.Wait(ready);
                    ReadBack(output, in frame, consumer);
                }
                finally
                {
                    output.Release();
                }

                return;
            }

            // Handed on at once: the frame carries the point the GPU signals once it is written.
            VideoFrame result = new(
                new VideoStorage(output.Describe(_engine.Identity, _engine.Ready(ready))),
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

    // Records the conversion of a frame into a pooled picture and runs it without waiting: the batch
    // waits on the GPU for the frame's producer, and the frame is kept until the batch has run.
    private ulong Convert(in VideoFrame frame, DmaBufImage image, PooledDmaBuf output)
    {
        VideoFrameLease input = frame.Retain();
        VulkanEngine.Batch batch;
        try
        {
            batch = _engine.Begin();
        }
        catch
        {
            input.Dispose();
            throw;
        }

        // A batch that ends unsubmitted lets go of the frame with its other holds.
        using (batch)
        {
            batch.Then(input.Dispose);
            if (!batch.WaitFor(in image))
            {
                throw new TimeoutException("The producer's GPU did not finish writing the frame.");
            }

            if (Info.Output.PixelFormat == PixelFormat.Nv12)
            {
                ToYuv(in batch, image, frame.Format, output);
            }
            else
            {
                ToRgb(in batch, image, frame.Format, frame.Color, output.Planes[0]);
            }

            _submitted = batch.Submit();
            return _submitted;
        }
    }

    // The result read back plane by plane once the GPU finished, handed on from memory; a consumer that
    // keeps it copies it.
    private void ReadBack(PooledDmaBuf output, in VideoFrame frame, IVideoFrameConsumer consumer)
    {
        VideoStreamDescription described = Info.Output;
        bool nv12 = described.PixelFormat == PixelFormat.Nv12;
        byte[] first = VulkanTransfer.Download(_engine, output.Planes[0], nv12 ? 1 : 4);
        byte[] second = nv12 ? VulkanTransfer.Download(_engine, output.Planes[1], 2) : [];
        VideoFrame result = new(
            new VideoFormat(described.PixelFormat, described.Size.Width, described.Size.Height),
            frame.Timestamp,
            first,
            output.Planes[0].Width * (nv12 ? 1 : 4),
            second,
            nv12 ? output.Planes[1].Width * 2 : 0,
            color: _outputColour,
            orientation: frame.Orientation,
            duration: frame.Duration
        );
        consumer.OnFrame(in result);
    }

    // RGB output is written by one compute pass, so it goes straight into a sink's DMA-BUF (a PipeWire
    // buffer) on this GPU, finished before this returns; NV12 output is assembled in surfaces of its own.
    public bool TryProcess(in VideoFrame frame, in VideoTarget target)
    {
        VideoStreamDescription output = Info.Output;
        if (
            output.PixelFormat == PixelFormat.Nv12
            || !frame.Storage.TryGetValue(out DmaBufImage image)
            || image.Sync is not null
            || !target.Storage.TryGetValue(out DmaBufImage into)
            || into.Sync is not null
            || into.Device != _engine.Identity
            || into.PlaneCount != 1
            || into.Modifier != VulkanEngine.LinearModifier
            || target.Format.PixelFormat != output.PixelFormat
            || target.Format.CodedSize != output.Size
        )
        {
            return false;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Written through an RGBA view whatever the memory order, as into a surface of its own.
            // The target names no sync point, so the drawing has finished when this returns.
            ImportCache.Lease lease = _engine.Imports.Import(
                into[0],
                into.Modifier,
                VkFormat.R8G8B8A8Unorm,
                output.Size.Width,
                output.Size.Height,
                VkImageUsageFlags.Storage
            );
            using VulkanEngine.Batch batch = _engine.Begin();
            batch.Then(lease.Dispose);
            if (!batch.WaitFor(in image) || !batch.WaitFor(in into, write: true))
            {
                throw new TimeoutException("The producer's GPU did not finish writing the frame.");
            }

            ToRgb(in batch, image, frame.Format, frame.Color, lease.Image);
            _submitted = batch.Submit();
            _engine.Wait(_submitted);
            return true;
        }
    }

    // Called by a consumer inside OnFrame, on the processing thread that holds the lock.
    public VideoFrameLease Retain(in VideoFrame frame)
    {
        PooledDmaBuf picture =
            _delivering
            ?? throw new InvalidOperationException("Frames are retained only while delivered.");
        picture.Hold();
        return new DmaBufFrameLease(in frame, picture);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                // The pool's pictures may still be written by the last batch.
                _disposed = true;
                _engine.Wait(_submitted);
                _pool.Dispose();
            }
        }
    }

    // BGRA or RGBA to NV12, packing alpha to the right when the output is twice the colour width.
    private void ToYuv(
        in VulkanEngine.Batch batch,
        DmaBufImage image,
        VideoFormat format,
        PooledDmaBuf output
    )
    {
        VkFormat sourceFormat =
            format.PixelFormat == PixelFormat.Rgba
                ? VkFormat.R8G8B8A8Unorm
                : VkFormat.B8G8R8A8Unorm;
        ImportCache.Lease sourceLease = _engine.Imports.Import(
            image[0],
            image.Modifier,
            sourceFormat,
            format.CodedSize.Width,
            format.CodedSize.Height,
            VkImageUsageFlags.Sampled
        );
        batch.Then(sourceLease.Dispose);
        VulkanImage source = sourceLease.Image;
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

        VulkanImage luma = output.Planes[0];
        VulkanImage chroma = output.Planes[1];
        VkImageView sourceView = source.View(sourceFormat);
        VkImageView lumaView = luma.View(VkFormat.R8Unorm);
        VkImageView chromaView = chroma.View(VkFormat.R8G8Unorm);
        ComputeKernel kernel = _engine.RgbToYuv;
        VkDescriptorSet set = _engine.AllocateSet(kernel.SetLayout);
        batch.Then(() =>
        {
            _engine.FreeSet(set);
            Destroy(sourceView, lumaView, chromaView);
        });
        {
            VkDescriptorImageInfo* images = stackalloc VkDescriptorImageInfo[3];
            images[0] = new VkDescriptorImageInfo
            {
                sampler = _engine.Sampler,
                imageView = sourceView,
                imageLayout = VkImageLayout.General,
            };
            images[1] = new VkDescriptorImageInfo
            {
                imageView = lumaView,
                imageLayout = VkImageLayout.General,
            };
            images[2] = new VkDescriptorImageInfo
            {
                imageView = chromaView,
                imageLayout = VkImageLayout.General,
            };
            VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[3];
            writes[0] = Recording.Sampled(set, 0, &images[0]);
            writes[1] = Recording.Storage(set, 1, &images[1]);
            writes[2] = Recording.Storage(set, 2, &images[2]);
            _engine.Api.vkUpdateDescriptorSets(3, writes, 0, null);

            Recording.Acquire(_engine, batch.Commands, [source, luma, chroma]);
            Dispatch(
                batch.Commands,
                kernel,
                set,
                &parameters,
                (uint)sizeof(RgbToYuvParameters),
                size.Width / 2,
                size.Height / 2
            );
            Recording.Release(_engine, batch.Commands, [luma, chroma]);
        }
    }

    // NV12 to BGRA or RGBA, taking alpha from the right half when the input carries it side by side.
    private void ToRgb(
        in VulkanEngine.Batch batch,
        DmaBufImage image,
        VideoFormat format,
        VideoColor inputColour,
        VulkanImage rgb
    )
    {
        int width = format.CodedSize.Width;
        int height = format.CodedSize.Height;
        ImportCache.Lease lumaLease = _engine.Imports.Import(
            image[0],
            image.Modifier,
            VkFormat.R8Unorm,
            width,
            height,
            VkImageUsageFlags.Storage
        );
        batch.Then(lumaLease.Dispose);
        VulkanImage luma = lumaLease.Image;
        ImportCache.Lease chromaLease = _engine.Imports.Import(
            image.PlaneCount > 1
                ? image[1]
                : image[0] with
                {
                    Offset = image[0].Offset + (image[0].Stride * height),
                },
            image.Modifier,
            VkFormat.R8G8Unorm,
            (width + 1) / 2,
            (height + 1) / 2,
            VkImageUsageFlags.Storage
        );
        batch.Then(chromaLease.Dispose);
        VulkanImage chroma = chromaLease.Image;

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
            SwapRedBlue = Info.Output.PixelFormat == PixelFormat.Bgra ? 1u : 0u,
            LumaScale = new Vector4(offset, scale, ColorConversion.ChromaCentre, 0),
            RowR = r,
            RowG = g,
            RowB = b,
        };

        VkImageView lumaView = luma.View(VkFormat.R8Unorm);
        VkImageView chromaView = chroma.View(VkFormat.R8G8Unorm);
        // Written through an RGBA view whatever the memory order; the kernel swaps for BGRA.
        VkImageView rgbView = rgb.View(VkFormat.R8G8B8A8Unorm);
        ComputeKernel kernel = _engine.YuvToRgb;
        VkDescriptorSet set = _engine.AllocateSet(kernel.SetLayout);
        batch.Then(() =>
        {
            _engine.FreeSet(set);
            Destroy(lumaView, chromaView, rgbView);
        });
        {
            VkDescriptorImageInfo* images = stackalloc VkDescriptorImageInfo[3];
            images[0] = new VkDescriptorImageInfo
            {
                imageView = lumaView,
                imageLayout = VkImageLayout.General,
            };
            images[1] = new VkDescriptorImageInfo
            {
                imageView = chromaView,
                imageLayout = VkImageLayout.General,
            };
            images[2] = new VkDescriptorImageInfo
            {
                imageView = rgbView,
                imageLayout = VkImageLayout.General,
            };
            VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[3];
            writes[0] = Recording.Storage(set, 0, &images[0]);
            writes[1] = Recording.Storage(set, 1, &images[1]);
            writes[2] = Recording.Storage(set, 2, &images[2]);
            _engine.Api.vkUpdateDescriptorSets(3, writes, 0, null);

            Recording.Acquire(_engine, batch.Commands, [luma, chroma, rgb]);
            Dispatch(
                batch.Commands,
                kernel,
                set,
                &parameters,
                (uint)sizeof(YuvToRgbParameters),
                size.Width,
                size.Height
            );
            Recording.Release(_engine, batch.Commands, [rgb]);
        }
    }

    private void Dispatch(
        VkCommandBuffer commands,
        ComputeKernel kernel,
        VkDescriptorSet set,
        void* parameters,
        uint size,
        int threadsX,
        int threadsY
    )
    {
        VkDeviceApi api = _engine.Api;
        api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Compute, kernel.Pipeline);
        api.vkCmdBindDescriptorSets(
            commands,
            VkPipelineBindPoint.Compute,
            kernel.Layout,
            0,
            1,
            &set
        );
        api.vkCmdPushConstants(
            commands,
            kernel.Layout,
            VkShaderStageFlags.Compute,
            0,
            size,
            parameters
        );
        api.vkCmdDispatch(commands, (uint)((threadsX + 7) / 8), (uint)((threadsY + 7) / 8), 1);
    }

    private void Destroy(params ReadOnlySpan<VkImageView> views)
    {
        foreach (VkImageView view in views)
        {
            _engine.Api.vkDestroyImageView(view, null);
        }
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
        public uint SwapRedBlue;
        public Vector4 LumaScale;
        public Vector4 RowR;
        public Vector4 RowG;
        public Vector4 RowB;
    }
}
