using System.Numerics;
using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Agash.StreamTransport.Windows.Direct3D12;

/// <summary>
/// Makes Direct3D 12 compute processors: RGB (BGRA or RGBA) to NV12 at any size, packing alpha side by
/// side when asked, and NV12 to RGB, unpacking side-by-side alpha when asked. Frames stay on the GPU
/// they arrive on.
/// </summary>
public sealed class D3D12VideoProcessorFactory : IVideoProcessorFactory
{
    private const string Name = "Direct3D 12 compute";

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

        // RGB in memory is uploaded as it is, for a sink or encoder on the GPU: what a software
        // decoder's frames, converted, become on the way to Spout.
        if (input.Storage == VideoStorageKind.Cpu)
        {
            return
                input.PixelFormat is PixelFormat.Bgra or PixelFormat.Rgba
                && output.Storages.Contains(VideoStorageKind.D3D12)
                && output.PixelFormats.Contains(input.PixelFormat)
                && processing.Alpha == AlphaLayout.None
                && (processing.Size is null || processing.Size == input.Size)
                ? Info(input.PixelFormat, input.Size, output.Device)
                : null;
        }

        // The work runs on the input's GPU; the result stays there, or is read back for a consumer in
        // memory (a software encoder fed by a GPU source).
        VideoStorageKind? result =
            output.Storages.Contains(VideoStorageKind.D3D12) ? VideoStorageKind.D3D12
            : output.Storages.Contains(VideoStorageKind.Cpu) ? VideoStorageKind.Cpu
            : null;
        if (
            input.Storage != VideoStorageKind.D3D12
            || result is not { } storage
            || (
                storage == VideoStorageKind.D3D12
                && output.Device is { } wanted
                && input.Device is { } actual
                && wanted != actual
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
    public IVideoProcessor Create(VideoStreamDescription input, VideoProcessing processing) =>
        new D3D12VideoProcessor(
            QueryCapabilities(input, processing)
                ?? throw new ArgumentException(
                    $"{Name} cannot turn {input} into what was asked.",
                    nameof(processing)
                ),
            processing
        );

    private static VideoProcessorInfo Info(
        PixelFormat format,
        VideoSize size,
        GpuIdentity? device,
        VideoStorageKind storage = VideoStorageKind.D3D12
    ) =>
        new(
            Name,
            IsHardwareAccelerated: true,
            new VideoStreamDescription(
                storage,
                format,
                size,
                storage == VideoStorageKind.D3D12 ? device : null
            )
        );
}

/// <summary>One stream's conversions on the GPU of its frames.</summary>
internal sealed unsafe class D3D12VideoProcessor(
    VideoProcessorInfo info,
    VideoProcessing processing
) : IVideoProcessor, IVideoFrameRetainer
{
    private readonly Lock _gate = new();
    private readonly Queue<(VideoFrameLease Input, ulong Done)> _reading = new();
    private D3D12Engine? _engine;
    private D3D12TexturePool? _pool;
    private D3D12TexturePool? _staging;
    private PooledTexture? _delivering;
    private nint _upload;
    private ulong _uploadSize;
    private ulong _uploaded;
    private nint _readback;
    private ulong _readbackSize;
    private bool _disposed;

    public VideoProcessorInfo Info { get; } = info;

    public void Process(in VideoFrame frame, IVideoFrameConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        if (frame.Storage.Kind == VideoStorageKind.Cpu)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                Upload(in frame, consumer);
            }

            return;
        }

        if (frame.Storage.Kind != VideoStorageKind.D3D12)
        {
            throw new ArgumentException("The frame is not a Direct3D 12 texture.", nameof(frame));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // The GPU reads the frame after this call returns. A producer that finishes a release queue
            // before reusing the texture lends it for as long as that queue waits for the read; any other
            // producer's frame is retained, since it may reuse what it lent once the call is over.
            _ = frame.Storage.TryGetValue(out D3D12Image lent);
            nint releaseQueue = lent.Sync.ReleaseQueue;
            VideoFrameLease? input = releaseQueue == 0 ? frame.Retain() : null;
            PooledTexture? output = null;
            D3D12Engine engine;
            ulong done;
            VideoColor colour;
            try
            {
                VideoFrame kept = input is null ? frame : input.Frame;
                _ = kept.Storage.TryGetValue(out D3D12Image image);
                engine = Engine(image.Resource);
                ReleaseFinishedInputs(engine);
                output = _pool!.Rent();
                engine.WaitForProducer(image.Sync);
                (done, colour) =
                    Info.Output.PixelFormat == PixelFormat.Nv12
                        ? ToYuv(engine, image, in kept, output)
                        : ToRgb(engine, image, in kept, output.Texture);
                if (releaseQueue != 0)
                {
                    engine.Release(releaseQueue, done);
                }
            }
            catch
            {
                input?.Dispose();
                output?.Release();
                throw;
            }

            if (input is not null)
            {
                _reading.Enqueue((input, done));
            }

            VideoStreamDescription described = Info.Output;
            if (described.Storage == VideoStorageKind.Cpu)
            {
                try
                {
                    ReadBack(engine, output, in frame, colour, consumer);
                }
                finally
                {
                    output.Release();
                }

                return;
            }

            VideoFrame result = new(
                new VideoStorage(
                    new D3D12Image(
                        output.Texture,
                        0,
                        engine.Adapter,
                        new D3D12Sync(Fence: engine.Fence, Value: done)
                    )
                ),
                new VideoFormat(described.PixelFormat, described.Size.Width, described.Size.Height),
                frame.Timestamp,
                color: colour,
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

    // RGB output is written by one compute pass, so it goes straight into a sink's texture (Spout's
    // shared texture) on this device; NV12 output is assembled from planes in surfaces of its own.
    public bool TryProcess(in VideoFrame frame, in VideoTarget target)
    {
        VideoStreamDescription output = Info.Output;
        if (
            output.PixelFormat == PixelFormat.Nv12
            || frame.Storage.Kind != VideoStorageKind.D3D12
            || !target.Storage.TryGetValue(out D3D12Image into)
            || into.Subresource != 0
            || target.Format.PixelFormat != output.PixelFormat
            || target.Format.CodedSize != output.Size
        )
        {
            return false;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _ = frame.Storage.TryGetValue(out D3D12Image lent);
            D3D12Engine engine = Engine(lent.Resource);
            if (!engine.Owns(into.Resource))
            {
                return false;
            }

            // The input is read in place or kept, as Process does.
            nint releaseQueue = lent.Sync.ReleaseQueue;
            VideoFrameLease? input = releaseQueue == 0 ? frame.Retain() : null;
            ulong done;
            try
            {
                VideoFrame kept = input is null ? frame : input.Frame;
                _ = kept.Storage.TryGetValue(out D3D12Image image);
                ReleaseFinishedInputs(engine);
                engine.WaitForProducer(image.Sync);
                engine.WaitForProducer(into.Sync);
                (done, _) = ToRgb(engine, image, in kept, into.Resource);
                if (releaseQueue != 0)
                {
                    engine.Release(releaseQueue, done);
                }
            }
            catch
            {
                input?.Dispose();
                throw;
            }

            if (input is not null)
            {
                _reading.Enqueue((input, done));
            }

            // The sink publishes after its queue has waited for the drawing; a target that names no queue
            // is published as soon as this returns, so the drawing finishes first.
            if (into.Sync.ReleaseQueue != 0)
            {
                engine.Release(into.Sync.ReleaseQueue, done);
            }
            else
            {
                engine.WaitFor(done);
            }

            return true;
        }
    }

    // Called by a consumer inside OnFrame, on the processing thread that holds the lock.
    public VideoFrameLease Retain(in VideoFrame frame)
    {
        PooledTexture texture =
            _delivering
            ?? throw new InvalidOperationException("Frames are retained only while delivered.");
        texture.Hold();
        return new D3D12FrameLease(in frame, texture);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_engine is { } engine)
            {
                engine.WaitFor(engine.Submitted);
                ReleaseFinishedInputs(engine);
            }

            _pool?.Dispose();
            _staging?.Dispose();
            if (_upload != 0)
            {
                D3D12Engine.Release(_upload);
            }

            if (_readback != 0)
            {
                D3D12Engine.Release(_readback);
            }

            _engine?.Dispose();
        }
    }

    // The result copied into the readback buffer after the GPU work, waited for, and handed on from the
    // mapped buffer; a consumer that keeps it copies it.
    private void ReadBack(
        D3D12Engine engine,
        PooledTexture output,
        in VideoFrame frame,
        VideoColor colour,
        IVideoFrameConsumer consumer
    )
    {
        VideoStreamDescription described = Info.Output;
        int planes = described.PixelFormat == PixelFormat.Nv12 ? 2 : 1;
        Span<D3D12_PLACED_SUBRESOURCE_FOOTPRINT> footprints =
            stackalloc D3D12_PLACED_SUBRESOURCE_FOOTPRINT[planes];
        Span<uint> rows = stackalloc uint[planes];
        ulong size = engine.Footprints(output.Texture, footprints, rows);
        if (size > _readbackSize)
        {
            if (_readback != 0)
            {
                D3D12Engine.Release(_readback);
            }

            _readback = engine.CreateReadbackBuffer(size);
            _readbackSize = size;
        }

        ID3D12GraphicsCommandList* list = engine.BeginCopy();
        for (int plane = 0; plane < planes; plane++)
        {
            D3D12Engine.CopyToBuffer(
                list,
                output.Texture,
                (uint)plane,
                _readback,
                footprints[plane]
            );
        }

        engine.WaitFor(engine.Submit());
        byte* mapped;
        D3D12_RANGE everything = new() { Begin = 0, End = (nuint)size };
        ((ID3D12Resource*)_readback)->Map(0, &everything, (void**)&mapped);
        try
        {
            ReadOnlySpan<byte> luma = new(
                mapped + footprints[0].Offset,
                (int)(footprints[0].Footprint.RowPitch * rows[0])
            );
            ReadOnlySpan<byte> chroma =
                planes > 1
                    ? new ReadOnlySpan<byte>(
                        mapped + footprints[1].Offset,
                        (int)(footprints[1].Footprint.RowPitch * rows[1])
                    )
                    : default;
            VideoFrame result = new(
                new VideoFormat(described.PixelFormat, described.Size.Width, described.Size.Height),
                frame.Timestamp,
                luma,
                (int)footprints[0].Footprint.RowPitch,
                chroma,
                planes > 1 ? (int)footprints[1].Footprint.RowPitch : 0,
                color: colour,
                orientation: frame.Orientation,
                duration: frame.Duration
            );
            consumer.OnFrame(in result);
        }
        finally
        {
            D3D12_RANGE nothing = default;
            ((ID3D12Resource*)_readback)->Unmap(0, &nothing);
        }
    }

    // A frame in memory, its rows written into the upload buffer at the texture's pitch and copied in.
    private void Upload(in VideoFrame frame, IVideoFrameConsumer consumer)
    {
        if (_engine is null)
        {
            nint device = D3D12Devices.Create(Info.Output.Device);
            try
            {
                _engine = new D3D12Engine(device);
            }
            finally
            {
                D3D12Engine.Release(device);
            }

            VideoStreamDescription described = Info.Output;
            _pool = new D3D12TexturePool(
                _engine,
                Format(described.PixelFormat),
                described.Size.Width,
                described.Size.Height
            );
        }

        D3D12Engine engine = _engine;
        PooledTexture output = _pool!.Rent();
        ulong done;
        try
        {
            D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint = engine.Footprint(
                output.Texture,
                out ulong size
            );
            if (size > _uploadSize)
            {
                engine.WaitFor(_uploaded);
                if (_upload != 0)
                {
                    D3D12Engine.Release(_upload);
                }

                _upload = engine.CreateUploadBuffer(size);
                _uploadSize = size;
            }

            // The buffer is rewritten only once the copy out of it finished.
            engine.WaitFor(_uploaded);
            _ = frame.Storage.TryGetValue(out CpuImage image);
            int stride = image.Planes[0].Stride;
            int rowBytes = frame.Format.VisibleRect.Width * 4;
            ReadOnlySpan<byte> pixels = frame.GetPlane(0);
            byte* mapped;
            ((ID3D12Resource*)_upload)->Map(0, null, (void**)&mapped);
            for (int row = 0; row < frame.Format.VisibleRect.Height; row++)
            {
                pixels
                    .Slice(row * stride, rowBytes)
                    .CopyTo(
                        new Span<byte>(
                            mapped
                                + (long)footprint.Offset
                                + ((long)row * footprint.Footprint.RowPitch),
                            rowBytes
                        )
                    );
            }

            ((ID3D12Resource*)_upload)->Unmap(0, null);
            ID3D12GraphicsCommandList* list = engine.BeginCopy();
            D3D12Engine.Transition(
                list,
                output.Texture,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST
            );
            D3D12Engine.CopyFromBuffer(list, output.Texture, _upload, footprint);
            D3D12Engine.Transition(
                list,
                output.Texture,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON
            );
            done = engine.Submit();
            _uploaded = done;
        }
        catch
        {
            output.Release();
            throw;
        }

        VideoStreamDescription result = Info.Output;
        VideoFrame uploaded = new(
            new VideoStorage(
                new D3D12Image(
                    output.Texture,
                    0,
                    engine.Adapter,
                    new D3D12Sync(Fence: engine.Fence, Value: done)
                )
            ),
            new VideoFormat(result.PixelFormat, result.Size.Width, result.Size.Height),
            frame.Timestamp,
            color: frame.Color,
            orientation: frame.Orientation,
            duration: frame.Duration,
            retainer: this
        );
        _delivering = output;
        try
        {
            consumer.OnFrame(in uploaded);
        }
        finally
        {
            _delivering = null;
            output.Release();
        }
    }

    private D3D12Engine Engine(nint resource)
    {
        nint device = D3D12Engine.DeviceOf(resource);
        try
        {
            if (_engine is null)
            {
                _engine = new D3D12Engine(device);
                VideoStreamDescription output = Info.Output;
                _pool = new D3D12TexturePool(
                    _engine,
                    Format(output.PixelFormat),
                    output.Size.Width,
                    output.Size.Height
                );
            }
            else if (_engine.Device != device)
            {
                throw new ArgumentException(
                    "The frame is on another device than the stream's earlier frames.",
                    nameof(resource)
                );
            }

            return _engine;
        }
        finally
        {
            D3D12Engine.Release(device);
        }
    }

    // RGB to NV12, packing alpha to the right when the output is twice the colour width.
    private (ulong Done, VideoColor Colour) ToYuv(
        D3D12Engine engine,
        D3D12Image image,
        in VideoFrame frame,
        PooledTexture output
    )
    {
        VideoColor colour = processing.Color ?? VideoColor.Bt709;
        (Vector4 y, Vector4 cb, Vector4 cr) = ColorConversion.RgbToYCbCr(colour);
        VideoSize size = Info.Output.Size;
        bool pack = processing.Alpha == AlphaLayout.PackSideBySide;
        RgbToYuvConstants constants = new()
        {
            OutWidth = (uint)size.Width,
            OutHeight = (uint)size.Height,
            ColourWidth = (uint)(pack ? size.Width / 2 : size.Width),
            Pack = pack ? 1u : 0u,
            RowY = y,
            RowCb = cb,
            RowCr = cr,
        };

        ID3D12GraphicsCommandList* list = engine.Begin(D3D12Shader.RgbToYuv);
        engine.ShaderResource(
            0,
            image.Resource,
            ViewFormat(image.Resource, frame.Format.PixelFormat),
            (uint)image.Subresource,
            0
        );
        engine.ShaderResource(1, 0, DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, 0, 0);
        if (output.Luma == 0)
        {
            // The NV12 texture itself, plane by plane: no second pass.
            engine.UnorderedAccess(0, output.Texture, DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, plane: 0);
            engine.UnorderedAccess(1, output.Texture, DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM, plane: 1);
            D3D12Engine.Transition(
                list,
                output.Texture,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS
            );
            list->SetComputeRoot32BitConstants(
                0,
                (uint)(sizeof(RgbToYuvConstants) / 4),
                &constants,
                0
            );
            list->Dispatch(Groups(size.Width / 2), Groups(size.Height / 2), 1);
            D3D12Engine.Transition(
                list,
                output.Texture,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON
            );
            return (engine.Submit(), colour);
        }

        engine.UnorderedAccess(0, output.Luma, DXGI_FORMAT.DXGI_FORMAT_R8_UNORM);
        engine.UnorderedAccess(1, output.Chroma, DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM);
        list->SetComputeRoot32BitConstants(0, (uint)(sizeof(RgbToYuvConstants) / 4), &constants, 0);
        list->Dispatch(Groups(size.Width / 2), Groups(size.Height / 2), 1);

        D3D12Engine.Transition(
            list,
            output.Luma,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE
        );
        D3D12Engine.Transition(
            list,
            output.Chroma,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE
        );
        D3D12Engine.Transition(
            list,
            output.Texture,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST
        );
        D3D12Engine.Copy(list, output.Texture, 0, output.Luma, 0);
        D3D12Engine.Copy(list, output.Texture, 1, output.Chroma, 0);
        D3D12Engine.Transition(
            list,
            output.Texture,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON
        );
        D3D12Engine.Transition(
            list,
            output.Luma,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS
        );
        D3D12Engine.Transition(
            list,
            output.Chroma,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS
        );
        return (engine.Submit(), colour);
    }

    // NV12 to RGB, taking alpha from the right half when the input carries it side by side.
    private (ulong Done, VideoColor Colour) ToRgb(
        D3D12Engine engine,
        D3D12Image image,
        in VideoFrame frame,
        nint output
    )
    {
        VideoColor source =
            frame.Color.Matrix == ColorMatrix.Unspecified
                ? VideoColor.Bt709 with
                {
                    Range = frame.Color.Range,
                }
                : frame.Color;
        (float offset, float scale, Vector4 r, Vector4 g, Vector4 b) = ColorConversion.YCbCrToRgb(
            source
        );
        VideoSize size = Info.Output.Size;
        YuvToRgbConstants constants = new()
        {
            OutWidth = (uint)size.Width,
            OutHeight = (uint)size.Height,
            Unpack = processing.Alpha == AlphaLayout.UnpackSideBySide ? 1u : 0u,
            LumaScale = new Vector4(offset, scale, ColorConversion.ChromaCentre, 0),
            RowR = r,
            RowG = g,
            RowB = b,
        };

        ID3D12GraphicsCommandList* list = engine.Begin(D3D12Shader.YuvToRgb);
        (nint planes, uint slice) = Readable(engine, list, image);
        engine.ShaderResource(0, planes, DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, slice, 0);
        engine.ShaderResource(1, planes, DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM, slice, 1);
        engine.UnorderedAccess(0, output, Format(Info.Output.PixelFormat));
        engine.UnorderedAccess(1, 0, DXGI_FORMAT.DXGI_FORMAT_R8_UNORM);
        list->SetComputeRoot32BitConstants(0, (uint)(sizeof(YuvToRgbConstants) / 4), &constants, 0);
        D3D12Engine.Transition(
            list,
            output,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS
        );
        list->Dispatch(Groups(size.Width), Groups(size.Height), 1);
        D3D12Engine.Transition(
            list,
            output,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON
        );
        return (engine.Submit(), VideoColor.Srgb);
    }

    // Decoder surfaces may deny shader access; those are copied into a readable NV12 texture first.
    private (nint Planes, uint Slice) Readable(
        D3D12Engine engine,
        ID3D12GraphicsCommandList* list,
        D3D12Image image
    )
    {
        D3D12_RESOURCE_DESC description = D3D12Engine.Describe(image.Resource);
        if (
            !description.Flags.HasFlag(
                D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_DENY_SHADER_RESOURCE
            )
        )
        {
            return (image.Resource, (uint)image.Subresource);
        }

        _staging ??= new D3D12TexturePool(
            engine,
            DXGI_FORMAT.DXGI_FORMAT_NV12,
            (int)description.Width,
            (int)description.Height
        );
        PooledTexture staging = _staging.Rent();
        uint arraySize = description.DepthOrArraySize;
        D3D12Engine.Transition(
            list,
            staging.Texture,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST
        );
        D3D12Engine.Copy(list, staging.Texture, 0, image.Resource, (uint)image.Subresource);
        D3D12Engine.Copy(
            list,
            staging.Texture,
            1,
            image.Resource,
            (uint)image.Subresource + arraySize
        );
        D3D12Engine.Transition(
            list,
            staging.Texture,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON
        );

        // The staging texture is reused only by later work on this queue, which runs after this.
        staging.Release();
        return (staging.Texture, 0);
    }

    private void ReleaseFinishedInputs(D3D12Engine engine)
    {
        ulong completed = engine.Completed;
        while (
            _reading.TryPeek(out (VideoFrameLease Input, ulong Done) oldest)
            && oldest.Done <= completed
        )
        {
            _ = _reading.Dequeue();
            oldest.Input.Dispose();
        }
    }

    // The SRV format of an RGB texture; a typeless texture is read as UNORM.
    private static DXGI_FORMAT ViewFormat(nint resource, PixelFormat format)
    {
        DXGI_FORMAT actual = D3D12Engine.Describe(resource).Format;
        return actual switch
        {
            DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM
            or DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM
            or DXGI_FORMAT.DXGI_FORMAT_B8G8R8X8_UNORM => actual,
            DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_TYPELESS => DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_TYPELESS => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
            _ => throw new NotSupportedException(
                $"A {format} frame in {actual} cannot be read as 8-bit UNORM RGB."
            ),
        };
    }

    private static DXGI_FORMAT Format(PixelFormat format) =>
        format switch
        {
            PixelFormat.Nv12 => DXGI_FORMAT.DXGI_FORMAT_NV12,
            PixelFormat.Bgra => DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            PixelFormat.Rgba => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };

    private static uint Groups(int threads) => (uint)((threads + 7) / 8);

    [StructLayout(LayoutKind.Sequential)]
    private struct RgbToYuvConstants
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
    private struct YuvToRgbConstants
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

/// <summary>A processor's output frame kept by a consumer; the texture returns to its pool on release.</summary>
internal sealed class D3D12FrameLease : VideoFrameLease, IVideoFrameRetainer
{
    private readonly PooledTexture _texture;

    public D3D12FrameLease(in VideoFrame frame, PooledTexture texture)
        : base(
            frame.Storage,
            frame.Format,
            frame.Timestamp,
            frame.Color,
            frame.Orientation,
            frame.Duration
        )
    {
        _texture = texture;
    }

    protected override IVideoFrameRetainer KeepAgain => this;

    public VideoFrameLease Retain(in VideoFrame frame)
    {
        _texture.Hold();
        return new D3D12FrameLease(in frame, _texture);
    }

    protected override void Release() => _texture.Release();
}
