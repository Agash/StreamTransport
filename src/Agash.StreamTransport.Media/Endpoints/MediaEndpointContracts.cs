using System.Collections.Immutable;

namespace Agash.StreamTransport.Media;

/// <summary>What an input is, which decides which inputs of different providers can stand in for each other.</summary>
public enum MediaInputKind
{
    /// <summary>A camera, a USB capture card or an HDMI receiver.</summary>
    Camera,

    /// <summary>Another application's output: OBS, VTube Studio, a game, shared over Spout, Syphon or PipeWire.</summary>
    Application,

    /// <summary>A screen or window capture.</summary>
    Screen,

    /// <summary>A stream on the network, such as NDI.</summary>
    Network,

    /// <summary>A microphone or line input.</summary>
    Microphone,

    /// <summary>What an audio output plays.</summary>
    Loopback,

    /// <summary>A generated signal, such as a test pattern or a test tone.</summary>
    Generated,

    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>One way a video input can deliver frames: a pixel format, a size and a frame rate.</summary>
/// <param name="PixelFormat">The pixel format it sends.</param>
/// <param name="Size">The frame size.</param>
/// <param name="FrameRate">Frames per second.</param>
public readonly record struct VideoInputMode(PixelFormat PixelFormat, VideoSize Size, double FrameRate)
{
    /// <inheritdoc/>
    public override string ToString() =>
        FormattableString.Invariant($"{Size.Width}x{Size.Height} {PixelFormat} @ {FrameRate:0.##}");
}

/// <summary>A video input a provider found.</summary>
/// <param name="Provider">The provider, as <see cref="IVideoInputProvider.Name"/>.</param>
/// <param name="Id">The provider's stable identifier for it: a device path, a sender name, a node id.</param>
/// <param name="Name">The name people know it by.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Modes">
/// The modes it can be opened in; empty when it delivers whatever its producer sends, as shared
/// application outputs do.
/// </param>
public sealed record VideoInputInfo(
    string Provider,
    string Id,
    string Name,
    MediaInputKind Kind,
    ImmutableArray<VideoInputMode> Modes
)
{
    /// <summary>
    /// What identifies the hardware behind the input across providers, such as a camera's device node;
    /// null when there is none. Two providers' inputs with one key are one device.
    /// </summary>
    public string? DeviceKey { get; init; }
}

/// <summary>An audio input a provider found.</summary>
/// <param name="Provider">The provider, as <see cref="IAudioInputProvider.Name"/>.</param>
/// <param name="Id">The provider's stable identifier for it.</param>
/// <param name="Name">The name people know it by.</param>
/// <param name="Kind">What it is.</param>
/// <param name="IsDefault">Whether the platform treats it as the default of its kind.</param>
public sealed record AudioInputInfo(
    string Provider,
    string Id,
    string Name,
    MediaInputKind Kind,
    bool IsDefault = false
);

/// <summary>What a video input should deliver; each unset part is chosen for the input.</summary>
public sealed record VideoInputRequest
{
    /// <summary>The frame size wanted; the largest the input has when null.</summary>
    public VideoSize? Size { get; init; }

    /// <summary>The frame rate wanted; the highest at the chosen size when null.</summary>
    public double? FrameRate { get; init; }

    /// <summary>The pixel format wanted; the input's cheapest to encode when null.</summary>
    public PixelFormat? PixelFormat { get; init; }

    /// <summary>The mode of <paramref name="modes"/> closest to this request, or null when there are none.</summary>
    /// <param name="modes">The modes an input offers.</param>
    /// <returns>The chosen mode.</returns>
    /// <remarks>
    /// The size nearest the requested one by area, or the largest; then the requested rate, or the
    /// highest; then the requested format, or the one cheapest to encode: NV12, the packed 4:2:2
    /// formats, then the rest.
    /// </remarks>
    public VideoInputMode? Choose(IEnumerable<VideoInputMode> modes)
    {
        ArgumentNullException.ThrowIfNull(modes);
        VideoInputMode? best = null;
        foreach (VideoInputMode mode in modes)
        {
            if (best is not { } current || Compare(mode, current) < 0)
            {
                best = mode;
            }
        }

        return best;
    }

    // Negative when a fits the request better than b.
    private int Compare(VideoInputMode a, VideoInputMode b)
    {
        long areaA = (long)a.Size.Width * a.Size.Height;
        long areaB = (long)b.Size.Width * b.Size.Height;
        int bySize = Size is { } size
            ? Math.Abs(areaA - ((long)size.Width * size.Height))
                .CompareTo(Math.Abs(areaB - ((long)size.Width * size.Height)))
            : areaB.CompareTo(areaA);
        if (bySize != 0)
        {
            return bySize;
        }

        int byRate = FrameRate is { } rate
            ? Math.Abs(a.FrameRate - rate).CompareTo(Math.Abs(b.FrameRate - rate))
            : b.FrameRate.CompareTo(a.FrameRate);
        if (byRate != 0)
        {
            return byRate;
        }

        return PixelFormat is { } format
            ? (b.PixelFormat == format).CompareTo(a.PixelFormat == format)
            : Cost(a.PixelFormat).CompareTo(Cost(b.PixelFormat));
    }

    private static int Cost(PixelFormat format) =>
        format switch
        {
            Media.PixelFormat.Nv12 => 0,
            Media.PixelFormat.Yuy2 or Media.PixelFormat.Uyvy => 1,
            Media.PixelFormat.I420 => 2,
            _ => 3,
        };
}

/// <summary>An open video input: frames flow to the consumers connected to it until it is disposed.</summary>
public interface IVideoInput : IVideoSource, IDisposable
{
    /// <summary>What was opened.</summary>
    VideoInputInfo Info { get; }

    /// <summary>The mode it captures in; null when it delivers whatever its producer sends.</summary>
    VideoInputMode? Mode { get; }
}

/// <summary>An open audio input.</summary>
public interface IAudioInput : IAudioSource, IDisposable
{
    /// <summary>What was opened.</summary>
    AudioInputInfo Info { get; }
}

/// <summary>A video output: received video published where other software can take it.</summary>
public interface IVideoOutput : IVideoSink, IDisposable
{
    /// <summary>The provider that made it.</summary>
    string Provider { get; }

    /// <summary>The name it is published under.</summary>
    string Name { get; }
}

/// <summary>An audio output: received audio played on a device or published to other software.</summary>
public interface IAudioOutput : IAudioSink, IDisposable
{
    /// <summary>The provider that made it.</summary>
    string Provider { get; }

    /// <summary>The device or name it plays to.</summary>
    string Name { get; }
}

/// <summary>
/// A family of video inputs: cameras through a platform API, shared application outputs, network
/// streams. Register one in DI to make its inputs available to <c>MediaDevices</c>; a host adds its own
/// (NDI, a game capture) the same way the built-in ones are added.
/// </summary>
public interface IVideoInputProvider
{
    /// <summary>The provider's name, used in input specs such as <c>spout:OBS</c>.</summary>
    string Name { get; }

    /// <summary>
    /// How strongly this provider is preferred for an input several of them find; higher wins. Native
    /// camera APIs rank above the FFmpeg fallback.
    /// </summary>
    int Rank { get; }

    /// <summary>The inputs present now.</summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The inputs; empty when the provider cannot run here.</returns>
    ValueTask<ImmutableArray<VideoInputInfo>> GetInputsAsync(CancellationToken cancellationToken);

    /// <summary>Opens an input this provider found.</summary>
    /// <param name="input">The input.</param>
    /// <param name="request">What to capture; ignored by inputs without modes.</param>
    /// <param name="cancellationToken">Cancels opening.</param>
    /// <returns>The open input.</returns>
    ValueTask<IVideoInput> OpenAsync(
        VideoInputInfo input,
        VideoInputRequest request,
        CancellationToken cancellationToken
    );
}

/// <summary>A family of audio inputs: devices of a platform audio API, loopback, network streams.</summary>
public interface IAudioInputProvider
{
    /// <summary>The provider's name, used in input specs.</summary>
    string Name { get; }

    /// <summary>How strongly this provider is preferred for an input several find; higher wins.</summary>
    int Rank { get; }

    /// <summary>The inputs present now.</summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The inputs; empty when the provider cannot run here.</returns>
    ValueTask<ImmutableArray<AudioInputInfo>> GetInputsAsync(CancellationToken cancellationToken);

    /// <summary>Opens an input this provider found.</summary>
    /// <param name="input">The input.</param>
    /// <param name="cancellationToken">Cancels opening.</param>
    /// <returns>The open input.</returns>
    ValueTask<IAudioInput> OpenAsync(AudioInputInfo input, CancellationToken cancellationToken);
}

/// <summary>A family of video outputs: Spout senders, Syphon servers, PipeWire nodes, network streams.</summary>
public interface IVideoOutputProvider
{
    /// <summary>The provider's name, used in output specs such as <c>syphon:Guest</c>.</summary>
    string Name { get; }

    /// <summary>Publishes an output under a name.</summary>
    /// <param name="name">The name other software sees.</param>
    /// <param name="cancellationToken">Cancels creation.</param>
    /// <returns>The output.</returns>
    ValueTask<IVideoOutput> CreateAsync(string name, CancellationToken cancellationToken);
}

/// <summary>A family of audio outputs: devices of a platform audio API, virtual nodes, network streams.</summary>
public interface IAudioOutputProvider
{
    /// <summary>The provider's name, used in output specs.</summary>
    string Name { get; }

    /// <summary>Makes an output.</summary>
    /// <param name="name">The device or published name; null for the default device.</param>
    /// <param name="cancellationToken">Cancels creation.</param>
    /// <returns>The output.</returns>
    ValueTask<IAudioOutput> CreateAsync(string? name, CancellationToken cancellationToken);
}
