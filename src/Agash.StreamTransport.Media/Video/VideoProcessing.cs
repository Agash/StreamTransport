namespace Agash.StreamTransport.Media;

/// <summary>How transparency travels through an opaque codec.</summary>
public enum AlphaLayout
{
    /// <summary>No alpha: the frames are opaque, or alpha is dropped.</summary>
    None,

    /// <summary>
    /// Colour and alpha side by side in one opaque frame twice as wide: colour on the left, alpha as luma
    /// on the right with neutral chroma. A processor packs frames with alpha into it before an encoder.
    /// </summary>
    PackSideBySide,

    /// <summary>The reverse of <see cref="PackSideBySide"/>: an opaque side-by-side frame back to one with alpha.</summary>
    UnpackSideBySide,
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
/// <param name="Color">The colour to convert to; null keeps the input's.</param>
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
}
