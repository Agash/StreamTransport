namespace Agash.StreamTransport.Media;

/// <summary>How transparency travels through an opaque codec.</summary>
public enum AlphaLayout
{
    /// <summary>No alpha: the frames are opaque, or alpha is dropped.</summary>
    None,

    /// <summary>
    /// Colour and alpha side by side in one opaque frame twice as wide: colour on the left, alpha as luma
    /// on the right with neutral chroma, in the frame's range (16-235 limited, 0-255 full). The colour
    /// width and height are even, so no 4:2:0 chroma block spans both halves. A processor packs frames
    /// with alpha into it before an encoder; any codec carries it.
    /// </summary>
    PackSideBySide,

    /// <summary>The reverse of <see cref="PackSideBySide"/>: an opaque side-by-side frame back to one with alpha.</summary>
    UnpackSideBySide,

    /// <summary>
    /// Alpha as the codec's own auxiliary picture layer, in the same access unit and RTP stream as the
    /// colour, which stays the base layer a decoder that ignores the layer shows opaque. H.265 has it
    /// (AUX_ALPHA with the alpha channel information SEI, RFC 7798 carries both layers); VVC (AUX_ALPHA,
    /// H.274 alpha information, RFC 9328) and AV2 define it the same way. Negotiated only where both the
    /// encoder and the decoder in use code the layer.
    /// </summary>
    Layer,
}

/// <summary>A stream of frames as a processor is fed them.</summary>
/// <param name="Storage">Where the frames are.</param>
/// <param name="PixelFormat">Their pixel format.</param>
/// <param name="Size">Their visible size.</param>
/// <param name="Device">The GPU they are on; null for CPU frames.</param>
public readonly record struct VideoStreamDescription(
    VideoStorageKind Storage,
    PixelFormat PixelFormat,
    VideoSize Size,
    GpuIdentity? Device = null
);

/// <summary>What a processor turns frames into.</summary>
/// <param name="Output">What the next stage accepts: storages, pixel formats and GPU.</param>
/// <param name="Size">The size to scale to; null keeps the input size (doubled in width when packing alpha).</param>
/// <param name="Color">
/// The colour to convert to; null keeps the input's. Processors convert the YCbCr matrix and range;
/// primaries and transfer are carried as stated and not converted, so a source in other primaries or an
/// HDR transfer is mapped before it reaches one. The GPU processors make 4:2:0 chroma as the average of
/// each 2x2 block, centre-sited.
/// </param>
/// <param name="Alpha">What to do with transparency.</param>
public sealed record VideoProcessing(
    VideoConstraints Output,
    VideoSize? Size = null,
    VideoColor? Color = null,
    AlphaLayout Alpha = AlphaLayout.None
);

/// <summary>What a processor produces.</summary>
/// <param name="ImplementationName">Which implementation, for logs.</param>
/// <param name="IsHardwareAccelerated">Whether it runs on a GPU.</param>
/// <param name="Output">The frames it produces.</param>
public sealed record VideoProcessorInfo(
    string ImplementationName,
    bool IsHardwareAccelerated,
    VideoStreamDescription Output
);

/// <summary>
/// Converts frames between pixel formats, sizes, colours, storages and alpha layouts, on one device.
/// Frames it hands on are valid for the call; they are retained through <see cref="VideoFrame.Retain"/>.
/// </summary>
public interface IVideoProcessor : IDisposable
{
    /// <summary>What it produces.</summary>
    VideoProcessorInfo Info { get; }

    /// <summary>Processes a frame and hands the result to the consumer.</summary>
    /// <param name="frame">The frame, as described when the processor was made.</param>
    /// <param name="consumer">Where the result goes.</param>
    void Process(in VideoFrame frame, IVideoFrameConsumer consumer);

    /// <summary>
    /// Processes a frame straight into a surface a sink lent, instead of a surface of its own. Its work
    /// is ordered before the sink publishes the surface as the target's storage asks.
    /// </summary>
    /// <param name="frame">The frame, as described when the processor was made.</param>
    /// <param name="target">The lent surface, in the format this processor produces.</param>
    /// <returns>
    /// Whether it rendered the frame; false, leaving the target untouched, when it cannot write that
    /// surface (another GPU, storage, format or size). By default a processor renders only into its own.
    /// </returns>
    bool TryProcess(in VideoFrame frame, in VideoTarget target) => false;
}

/// <summary>Makes video processors of one family.</summary>
public interface IVideoProcessorFactory
{
    /// <summary>How strongly this family is preferred when several can do the job; higher wins.</summary>
    int Rank { get; }

    /// <summary>What a processor for a stream and a request would produce; null when it cannot.</summary>
    /// <param name="input">The frames it would be fed.</param>
    /// <param name="processing">What to turn them into.</param>
    /// <returns>The processor's information, or null.</returns>
    VideoProcessorInfo? QueryCapabilities(VideoStreamDescription input, VideoProcessing processing);

    /// <summary>Makes a processor.</summary>
    /// <param name="input">The frames it will be fed.</param>
    /// <param name="processing">What to turn them into.</param>
    /// <returns>The processor.</returns>
    IVideoProcessor Create(VideoStreamDescription input, VideoProcessing processing);
}

/// <summary>
/// A consumer of frames that states what it accepts, so a producer (a decoder, a processor) can hand it
/// frames it takes without a conversion.
/// </summary>
public interface IVideoSink : IVideoFrameConsumer
{
    /// <summary>The storages, formats and GPU it accepts.</summary>
    VideoConstraints Constraints { get; }

    /// <summary>
    /// Lends the surface the sink publishes its next frame from, for the length of the call: the renderer
    /// draws the frame into it, and the sink publishes it when the renderer returns true, so the picture
    /// is never copied into the sink.
    /// </summary>
    /// <typeparam name="TState">What the renderer needs, a borrowed frame included.</typeparam>
    /// <param name="format">The frame to be drawn, in a format and size the sink accepts.</param>
    /// <param name="state">Passed to the renderer.</param>
    /// <param name="render">Draws the frame into the target and returns whether it did.</param>
    /// <returns>
    /// Whether the frame was drawn and published, skipped, or left to
    /// <see cref="IVideoFrameConsumer.OnFrame"/>. By default a sink has no surface to lend.
    /// </returns>
    VideoRenderResult Render<TState>(
        VideoFormat format,
        scoped in TState state,
        VideoTargetRenderer<TState> render
    )
        where TState : allows ref struct => VideoRenderResult.Unavailable;
}

/// <summary>What became of a frame offered to a sink to draw into (<see cref="IVideoSink.Render"/>).</summary>
public enum VideoRenderResult
{
    /// <summary>
    /// The sink lent no surface for it, having none of its own or none for this format yet, or the
    /// renderer could not draw into the one lent: hand the frame to <see cref="IVideoFrameConsumer.OnFrame"/>.
    /// </summary>
    Unavailable,

    /// <summary>Drawn into the sink's surface and published.</summary>
    Rendered,

    /// <summary>
    /// Skipped: the sink's surfaces are all in use and a newer frame takes the next one, so the frame is
    /// not converted at all.
    /// </summary>
    Skipped,
}

/// <summary>
/// A surface a sink lends for one frame: the one it publishes from, for what produces the frame to draw
/// into directly.
/// </summary>
/// <param name="Storage">
/// The surface. Its synchronisation says how drawing is ordered before the sink publishes: a Direct3D 12
/// target names the queue (<see cref="D3D12Sync.ReleaseQueue"/>) to make wait for the drawing; an
/// IOSurface target names a Metal shared event (<see cref="IOSurfaceImage.SharedEvent"/>) for the drawing
/// to signal to <see cref="IOSurfaceImage.SignalValue"/> when it completes; a target that names nothing
/// is published as soon as the renderer returns, so the drawing has finished by then.
/// </param>
/// <param name="Format">The frame the surface holds.</param>
public readonly record struct VideoTarget(VideoStorage Storage, VideoFormat Format);

/// <summary>Draws a frame into a surface a sink lent (<see cref="IVideoSink.Render"/>).</summary>
/// <typeparam name="TState">What it needs, a borrowed frame included.</typeparam>
/// <param name="target">The lent surface.</param>
/// <param name="state">What the caller passed.</param>
/// <returns>Whether it drew the frame; false leaves the surface unpublished.</returns>
public delegate bool VideoTargetRenderer<TState>(in VideoTarget target, scoped in TState state)
    where TState : allows ref struct;
