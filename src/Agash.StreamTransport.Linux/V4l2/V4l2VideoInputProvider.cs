using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Agash.StreamTransport.Linux.V4l2.V4l2Native;

namespace Agash.StreamTransport.Linux.V4l2;

/// <summary>
/// Cameras, USB capture cards and HDMI receivers through Video4Linux2: every <c>/dev/video*</c> node that
/// captures video by streaming I/O, single- or multi-planar, in the uncompressed formats the media model
/// has. Frames are the driver's own buffers, lent to consumers without a copy, as DMA-BUFs to a consumer
/// on a GPU that takes them and as mapped memory otherwise; timestamps are the driver's capture times.
/// </summary>
public sealed unsafe partial class V4l2VideoInputProvider : IVideoInputProvider
{
    // The sizes worth offering from a device that describes a continuous or stepwise range.
    private static readonly VideoSize[] CommonSizes =
    [
        new(3840, 2160),
        new(2560, 1440),
        new(1920, 1080),
        new(1280, 720),
        new(640, 480),
    ];

    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly string _directory;

    /// <summary>The provider over <c>/dev</c>.</summary>
    /// <param name="loggerFactory">Where inputs log.</param>
    public V4l2VideoInputProvider(ILoggerFactory? loggerFactory = null)
        : this("/dev", loggerFactory) { }

    internal V4l2VideoInputProvider(string directory, ILoggerFactory? loggerFactory)
    {
        _directory = directory;
        _loggers = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggers.CreateLogger<V4l2VideoInputProvider>();
    }

    /// <inheritdoc/>
    public string Name => "v4l2";

    /// <inheritdoc/>
    public int Rank => 100;

    /// <inheritdoc/>
    public ValueTask<ImmutableArray<VideoInputInfo>> GetInputsAsync(
        CancellationToken cancellationToken
    )
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(_directory))
        {
            return ValueTask.FromResult(ImmutableArray<VideoInputInfo>.Empty);
        }

        List<VideoInputInfo> inputs = [];
        foreach (
            string path in Directory.EnumerateFiles(_directory, "video*").Order(NodeOrder.Instance)
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Describe(path) is { } input)
            {
                inputs.Add(input);
            }
        }

        // Two devices of one model share a card name; their paths tell them apart.
        HashSet<string> shared =
        [
            .. inputs
                .GroupBy(static i => i.Name)
                .Where(static g => g.Count() > 1)
                .Select(static g => g.Key),
        ];
        for (int i = 0; i < inputs.Count; i++)
        {
            if (shared.Contains(inputs[i].Name))
            {
                inputs[i] = inputs[i] with { Name = $"{inputs[i].Name} ({inputs[i].Id})" };
            }
        }

        return ValueTask.FromResult(inputs.ToImmutableArray());
    }

    /// <inheritdoc/>
    public ValueTask<IVideoInput> OpenAsync(
        VideoInputInfo input,
        VideoInputRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(request);
        VideoInputMode mode =
            request.Choose(input.Modes)
            ?? throw new NotSupportedException($"{input.Name} has no mode this provider delivers.");
        return ValueTask.FromResult<IVideoInput>(new V4l2VideoInput(input, mode, _loggers));
    }

    // The node as an input, or null when it is not a streaming video capture node.
    private VideoInputInfo? Describe(string path)
    {
        int fd = Open(path, ORdWr | ONonBlock | OCloExec);
        if (fd < 0)
        {
            LogUnopenable(path, Marshal.GetLastPInvokeError());
            return null;
        }

        try
        {
            Capability capability = default;
            if (Control(fd, QueryCap, ref capability) < 0)
            {
                return null;
            }

            uint caps = capability.Effective;
            uint type =
                (caps & CapVideoCapture) != 0 ? BufTypeVideoCapture
                : (caps & CapVideoCaptureMplane) != 0 ? BufTypeVideoCaptureMplane
                : 0;
            if (type == 0 || (caps & CapStreaming) == 0)
            {
                return null;
            }

            ImmutableArray<VideoInputMode> modes = Modes(fd, type);
            string card = Text(new ReadOnlySpan<byte>(capability.Card, 32));
            if (modes.IsEmpty)
            {
                LogNoModes(card, path);
                return null;
            }

            return new VideoInputInfo(Name, path, card, MediaInputKind.Camera, modes)
            {
                DeviceKey = path,
            };
        }
        finally
        {
            _ = Close(fd);
        }
    }

    private static ImmutableArray<VideoInputMode> Modes(int fd, uint type)
    {
        ImmutableArray<VideoInputMode>.Builder modes =
            ImmutableArray.CreateBuilder<VideoInputMode>();
        for (uint index = 0; ; index++)
        {
            FmtDesc description = new() { Index = index, Type = type };
            if (Control(fd, EnumFmt, ref description) < 0)
            {
                break;
            }

            if (
                (description.Flags & FmtFlagCompressed) != 0
                || V4l2Formats.ToMedia(description.PixelFormat) is not { } format
            )
            {
                continue;
            }

            foreach (VideoSize size in Sizes(fd, description.PixelFormat))
            {
                foreach (double rate in Rates(fd, description.PixelFormat, size))
                {
                    VideoInputMode mode = new(format, size, rate);
                    if (!modes.Contains(mode))
                    {
                        modes.Add(mode);
                    }
                }
            }
        }

        return modes.ToImmutable();
    }

    private static List<VideoSize> Sizes(int fd, uint fourcc)
    {
        List<VideoSize> sizes = [];
        for (uint index = 0; ; index++)
        {
            FrmSizeEnum size = new() { Index = index, PixelFormat = fourcc };
            if (Control(fd, EnumFrameSizes, ref size) < 0)
            {
                break;
            }

            if (size.Type == FrmSizeDiscrete)
            {
                sizes.Add(new VideoSize((int)size.Width, (int)size.Height));
                continue;
            }

            // A range: its largest size and the common sizes inside it.
            uint minWidth = size.Width;
            sizes.Add(new VideoSize((int)size.MaxWidth, (int)size.MaxHeight));
            sizes.AddRange(
                CommonSizes.Where(s =>
                    s.Width >= minWidth
                    && s.Width <= size.MaxWidth
                    && s.Height <= size.MaxHeight
                    && !(s.Width == size.MaxWidth && s.Height == size.MaxHeight)
                )
            );
            break;
        }

        return sizes;
    }

    private static List<double> Rates(int fd, uint fourcc, VideoSize size)
    {
        List<double> rates = [];
        for (uint index = 0; ; index++)
        {
            FrmIvalEnum interval = new()
            {
                Index = index,
                PixelFormat = fourcc,
                Width = (uint)size.Width,
                Height = (uint)size.Height,
            };
            if (Control(fd, EnumFrameIntervals, ref interval) < 0 || interval.Numerator == 0)
            {
                break;
            }

            // A discrete interval, or the shortest of a range.
            rates.Add(Math.Round((double)interval.Denominator / interval.Numerator, 3));
            if (interval.Type != FrmIvalDiscrete)
            {
                break;
            }
        }

        // A device that lists no intervals runs at whatever rate it runs at.
        if (rates.Count == 0)
        {
            rates.Add(30);
        }

        return rates;
    }

    [LoggerMessage(2560, LogLevel.Debug, "{Path} could not be opened ({Errno}); it is not listed.")]
    private partial void LogUnopenable(string path, int errno);

    [LoggerMessage(
        2561,
        LogLevel.Information,
        "{Card} at {Path} has no uncompressed format this provider delivers; it is not listed."
    )]
    private partial void LogNoModes(string card, string path);

    // video2 before video10.
    private sealed class NodeOrder : IComparer<string>
    {
        public static NodeOrder Instance { get; } = new();

        public int Compare(string? x, string? y) => Number(x).CompareTo(Number(y));

        private static int Number(string? path) =>
            int.TryParse(
                (Path.GetFileName(path) ?? string.Empty).AsSpan("video".Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int n
            )
                ? n
                : int.MaxValue;
    }
}

// The V4L2 pixel formats the media model has, by fourcc.
internal static class V4l2Formats
{
    public static readonly uint Yuyv = FourCc("YUYV");
    public static readonly uint Uyvy = FourCc("UYVY");
    public static readonly uint Nv12 = FourCc("NV12");
    public static readonly uint Nv12M = FourCc("NM12");
    public static readonly uint Yu12 = FourCc("YU12");
    public static readonly uint Abgr32 = FourCc("AR24");
    public static readonly uint Rgba32 = FourCc("AB24");

    public static PixelFormat? ToMedia(uint fourcc) =>
        fourcc == Yuyv ? PixelFormat.Yuy2
        : fourcc == Uyvy ? PixelFormat.Uyvy
        : fourcc == Nv12 || fourcc == Nv12M ? PixelFormat.Nv12
        : fourcc == Yu12 ? PixelFormat.I420
        : fourcc == Abgr32 ? PixelFormat.Bgra
        : fourcc == Rgba32 ? PixelFormat.Rgba
        : null;

    // The fourccs a media format is captured in, single-buffer layouts first.
    public static uint[] FromMedia(PixelFormat format) =>
        format switch
        {
            PixelFormat.Yuy2 => [Yuyv],
            PixelFormat.Uyvy => [Uyvy],
            PixelFormat.Nv12 => [Nv12, Nv12M],
            PixelFormat.I420 => [Yu12],
            PixelFormat.Bgra => [Abgr32],
            PixelFormat.Rgba => [Rgba32],
            _ => [],
        };

    // The DRM fourcc of a buffer in this format: the same codes, except the RGB orders.
    public static uint DrmFourCc(PixelFormat format) =>
        format switch
        {
            PixelFormat.Bgra => FourCc("AR24"),
            PixelFormat.Rgba => FourCc("AB24"),
            PixelFormat.I420 => FourCc("YU12"),
            PixelFormat.Nv12 => FourCc("NV12"),
            PixelFormat.Yuy2 => FourCc("YUYV"),
            PixelFormat.Uyvy => FourCc("UYVY"),
            _ => 0,
        };
}
