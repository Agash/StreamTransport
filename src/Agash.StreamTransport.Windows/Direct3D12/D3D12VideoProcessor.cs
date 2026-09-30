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
        if (
            input.Storage != VideoStorageKind.D3D12
            || !output.Storages.Contains(VideoStorageKind.D3D12)
            || (output.Device is { } wanted && input.Device is { } actual && wanted != actual)
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
            return size.Width % 2 == 0 && size.Height % 2 == 0
                ? Info(PixelFormat.Nv12, size, input.Device)
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
                    return Info(format, colour, input.Device);
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
        GpuIdentity? device
    ) =>
        new(
            Name,
            IsHardwareAccelerated: true,
            new VideoStreamDescription(VideoStorageKind.D3D12, format, size, device)
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
    private bool _disposed;

    public VideoProcessorInfo Info { get; } = info;

    public void Process(in VideoFrame frame, IVideoFrameConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        if (!frame.Storage.TryGetValue(out D3D12Image image))
        {
            throw new ArgumentException("The frame is not a Direct3D 12 texture.", nameof(frame));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            D3D12Engine engine = Engine(image.Resource);
            ReleaseFinishedInputs(engine);

            PooledTexture output = _pool!.Rent();
            VideoFrameLease input = frame.Retain();
            ulong done;
            VideoColor colour;
            try
            {
                engine.WaitForProducer(image.Sync);
                (done, colour) =
                    Info.Output.PixelFormat == PixelFormat.Nv12
                        ? ToYuv(engine, image, in frame, output)
                        : ToRgb(engine, image, in frame, output);
            }
            catch
            {
                input.Dispose();
                output.Release();
                throw;
            }

            _reading.Enqueue((input, done));
            VideoStreamDescription described = Info.Output;
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
            _engine?.Dispose();
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
        PooledTexture output
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
        engine.UnorderedAccess(0, output.Texture, Format(Info.Output.PixelFormat));
        engine.UnorderedAccess(1, 0, DXGI_FORMAT.DXGI_FORMAT_R8_UNORM);
        list->SetComputeRoot32BitConstants(0, (uint)(sizeof(YuvToRgbConstants) / 4), &constants, 0);
        D3D12Engine.Transition(
            list,
            output.Texture,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS
        );
        list->Dispatch(Groups(size.Width), Groups(size.Height), 1);
        D3D12Engine.Transition(
            list,
            output.Texture,
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
            DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM or DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM =>
                actual,
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
