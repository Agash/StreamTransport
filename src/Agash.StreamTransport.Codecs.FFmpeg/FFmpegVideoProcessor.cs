using Agash.StreamTransport.Media;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Codecs.FFmpeg;

/// <summary>
/// Makes CPU video processors on FFmpeg's swscale: pixel format, size and colour conversion between
/// frames in system memory, and side-by-side alpha packed and unpacked as the GPU processors do it
/// (alpha as video-range luma beside the colour, with neutral chroma). It ranks below every GPU
/// processor.
/// </summary>
public sealed class FFmpegVideoProcessorFactory : IVideoProcessorFactory
{
    private const string Name = "FFmpeg swscale";

    /// <inheritdoc/>
    public int Rank => 10;

    /// <inheritdoc/>
    public VideoProcessorInfo? QueryCapabilities(
        VideoStreamDescription input,
        VideoProcessing processing
    )
    {
        ArgumentNullException.ThrowIfNull(processing);
        if (
            input.Storage != VideoStorageKind.Cpu
            || processing.Alpha
                is not (
                    AlphaLayout.None
                    or AlphaLayout.PackSideBySide
                    or AlphaLayout.UnpackSideBySide
                )
            || !processing.Output.Storages.Contains(VideoStorageKind.Cpu)
            || !FF.Scaler.IsSupportedInput(Formats.ToFFmpeg(input.PixelFormat))
        )
        {
            return null;
        }

        if (processing.Alpha == AlphaLayout.PackSideBySide)
        {
            // Colour with alpha in, an opaque 4:2:0 frame twice as wide out.
            VideoSize colour = processing.Size ?? input.Size;
            PixelFormat? packed = processing.Output.PixelFormats.FirstOrDefault(static f =>
                f is PixelFormat.Nv12 or PixelFormat.I420
            );
            return
                HasAlpha(input.PixelFormat)
                && packed is PixelFormat.Nv12 or PixelFormat.I420
                && colour.Width % 2 == 0
                && colour.Height % 2 == 0
                ? Info(packed.Value, colour with { Width = colour.Width * 2 })
                : null;
        }

        if (processing.Alpha == AlphaLayout.UnpackSideBySide)
        {
            // An opaque side-by-side frame in, the colour with its alpha out, half as wide.
            PixelFormat? unpacked = processing.Output.PixelFormats.FirstOrDefault(HasAlpha);
            VideoSize colour = input.Size with { Width = input.Size.Width / 2 };
            return
                input.PixelFormat is PixelFormat.Nv12 or PixelFormat.I420
                && unpacked is { } format
                && HasAlpha(format)
                && input.Size.Width % 4 == 0
                && (processing.Size is null || processing.Size == colour)
                ? Info(format, colour)
                : null;
        }

        foreach (PixelFormat format in processing.Output.PixelFormats)
        {
            if (FF.Scaler.IsSupportedOutput(Formats.ToFFmpeg(format)))
            {
                return Info(format, processing.Size ?? input.Size);
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public IVideoProcessor Create(VideoStreamDescription input, VideoProcessing processing) =>
        new FFmpegVideoProcessor(
            QueryCapabilities(input, processing)
                ?? throw new ArgumentException(
                    $"{Name} cannot turn {input} into what was asked.",
                    nameof(processing)
                ),
            processing.Color,
            processing.Alpha
        );

    private static bool HasAlpha(PixelFormat format) =>
        format is PixelFormat.Bgra or PixelFormat.Rgba or PixelFormat.Yuva420;

    private static VideoProcessorInfo Info(PixelFormat format, VideoSize size) =>
        new(
            Name,
            IsHardwareAccelerated: false,
            new VideoStreamDescription(VideoStorageKind.Cpu, format, size)
        );
}

// Converts one CPU frame at a time; each result is a fresh FFmpeg frame, so a retained result stays
// valid while the next is made.
internal sealed class FFmpegVideoProcessor(
    VideoProcessorInfo info,
    VideoColor? color,
    AlphaLayout alpha
) : IVideoProcessor, IVideoFrameRetainer
{
    private readonly FF.Scaler _scaler = new();
    private readonly FF.Frame _source = new();
    private readonly FF.Frame _output = new();

    // The intermediate picture of a side-by-side pack or unpack.
    private readonly FF.Frame _work = new();
    private readonly Lock _gate = new();
    private bool _delivering;
    private bool _disposed;

    public VideoProcessorInfo Info { get; } = info;

    public void Process(in VideoFrame frame, IVideoFrameConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            FFmpegFrames.CopyFrom(in frame, _source);
            SetColor(_source, frame.Color);

            VideoStreamDescription output = Info.Output;
            VideoColor target = color ?? frame.Color.ConvertedTo(output.PixelFormat);
            switch (alpha)
            {
                case AlphaLayout.PackSideBySide:
                    Pack(output, target);
                    break;
                case AlphaLayout.UnpackSideBySide:
                    Unpack(output, target);
                    break;
                default:
                    // The scaler releases the previous result's buffer (a retained one keeps its
                    // reference) and produces what the frame describes.
                    _output.Width = output.Size.Width;
                    _output.Height = output.Size.Height;
                    _output.PixelFormat = Formats.ToFFmpeg(output.PixelFormat);
                    SetColor(_output, target);
                    _scaler.Scale(_source, _output);
                    break;
            }

            _delivering = true;
            try
            {
                consumer.OnFrame(
                    FFmpegFrames.View(
                        _output,
                        new VideoFormat(output.PixelFormat, output.Size.Width, output.Size.Height),
                        frame.Timestamp,
                        target,
                        this
                    )
                );
            }
            finally
            {
                _delivering = false;
            }
        }
    }

    // Called by a consumer inside OnFrame, on the processing thread that holds the lock.
    public VideoFrameLease Retain(in VideoFrame frame) =>
        _delivering
            ? new FFmpegFrameLease(in frame, _output.Clone())
            : throw new InvalidOperationException("Frames are retained only while delivered.");

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _scaler.Dispose();
            _source.Dispose();
            _output.Dispose();
            _work.Dispose();
        }
    }

    // Colour on the left, alpha as video-range luma on the right with neutral chroma: the colour and
    // alpha come out of one conversion to 4:2:0 with alpha, then are laid side by side.
    private void Pack(VideoStreamDescription output, VideoColor target)
    {
        int width = output.Size.Width / 2;
        int height = output.Size.Height;
        _work.Width = width;
        _work.Height = height;
        _work.PixelFormat = Formats.Yuva420P;
        SetColor(_work, target);
        _scaler.Scale(_source, _work);

        _output.AllocateVideo(output.Size.Width, height, Formats.ToFFmpeg(output.PixelFormat));
        SetColor(_output, target);
        FF.ReadOnlyImagePlane y = _work.GetPlane(0);
        FF.ReadOnlyImagePlane u = _work.GetPlane(1);
        FF.ReadOnlyImagePlane v = _work.GetPlane(2);

        // Alpha straight from the source when the size stays, since swscale's alpha path rounds;
        // from the converted picture when it was scaled.
        PixelFormat? source = Formats.FromFFmpeg(_source.PixelFormat);
        bool direct = _source.Width == width && _source.Height == height;
        bool packedRgb = direct && source is PixelFormat.Bgra or PixelFormat.Rgba;
        FF.ReadOnlyImagePlane a =
            packedRgb ? _source.GetPlane(0)
            : direct ? _source.GetPlane(3)
            : _work.GetPlane(3);
        FF.ImagePlane luma = _output.GetWritablePlane(0);
        for (int row = 0; row < height; row++)
        {
            Span<byte> line = luma.GetRow(row);
            y.GetRow(row)[..width].CopyTo(line);
            ReadOnlySpan<byte> alphaRow = a.GetRow(row);
            for (int x = 0; x < width; x++)
            {
                line[width + x] = ToVideoRange(packedRgb ? alphaRow[(4 * x) + 3] : alphaRow[x]);
            }
        }

        int chromaWidth = width / 2;
        if (output.PixelFormat == PixelFormat.Nv12)
        {
            FF.ImagePlane chroma = _output.GetWritablePlane(1);
            for (int row = 0; row < height / 2; row++)
            {
                Span<byte> line = chroma.GetRow(row);
                ReadOnlySpan<byte> uRow = u.GetRow(row);
                ReadOnlySpan<byte> vRow = v.GetRow(row);
                for (int x = 0; x < chromaWidth; x++)
                {
                    line[2 * x] = uRow[x];
                    line[(2 * x) + 1] = vRow[x];
                }

                line[(2 * chromaWidth)..(4 * chromaWidth)].Fill(Neutral);
            }
        }
        else
        {
            for (int plane = 1; plane <= 2; plane++)
            {
                FF.ReadOnlyImagePlane from = plane == 1 ? u : v;
                FF.ImagePlane to = _output.GetWritablePlane(plane);
                for (int row = 0; row < height / 2; row++)
                {
                    Span<byte> line = to.GetRow(row);
                    from.GetRow(row)[..chromaWidth].CopyTo(line);
                    line[chromaWidth..(2 * chromaWidth)].Fill(Neutral);
                }
            }
        }
    }

    // The left half's colour converted whole, with alpha read back from the right half's luma.
    private void Unpack(VideoStreamDescription output, VideoColor target)
    {
        int width = output.Size.Width;
        int height = output.Size.Height;
        _work.Width = width * 2;
        _work.Height = height;
        _work.PixelFormat = Formats.ToFFmpeg(output.PixelFormat);
        SetColor(_work, target);
        _scaler.Scale(_source, _work);

        _output.AllocateVideo(width, height, Formats.ToFFmpeg(output.PixelFormat));
        SetColor(_output, target);
        FF.ReadOnlyImagePlane packedLuma = _source.GetPlane(0);
        if (output.PixelFormat == PixelFormat.Yuva420)
        {
            for (int plane = 0; plane < 3; plane++)
            {
                int planeWidth = plane == 0 ? width : width / 2;
                int planeHeight = plane == 0 ? height : height / 2;
                FF.ReadOnlyImagePlane from = _work.GetPlane(plane);
                FF.ImagePlane to = _output.GetWritablePlane(plane);
                for (int row = 0; row < planeHeight; row++)
                {
                    from.GetRow(row)[..planeWidth].CopyTo(to.GetRow(row));
                }
            }

            FF.ImagePlane alphaPlane = _output.GetWritablePlane(3);
            for (int row = 0; row < height; row++)
            {
                Span<byte> line = alphaPlane.GetRow(row);
                ReadOnlySpan<byte> packed = packedLuma.GetRow(row);
                for (int x = 0; x < width; x++)
                {
                    line[x] = FromVideoRange(packed[width + x]);
                }
            }

            return;
        }

        FF.ReadOnlyImagePlane rgb = _work.GetPlane(0);
        FF.ImagePlane pixels = _output.GetWritablePlane(0);
        for (int row = 0; row < height; row++)
        {
            Span<byte> line = pixels.GetRow(row);
            rgb.GetRow(row)[..(width * 4)].CopyTo(line);
            ReadOnlySpan<byte> packed = packedLuma.GetRow(row);
            for (int x = 0; x < width; x++)
            {
                line[(4 * x) + 3] = FromVideoRange(packed[width + x]);
            }
        }
    }

    private const byte Neutral = 128;

    private static byte ToVideoRange(byte alpha) => (byte)(16 + (((219 * alpha) + 127) / 255));

    private static byte FromVideoRange(byte luma) =>
        (byte)Math.Clamp((((luma - 16) * 255) + 109) / 219, 0, 255);

    private static void SetColor(FF.Frame frame, VideoColor color)
    {
        frame.ColorSpace = Formats.ToFFmpeg(color.Matrix);
        frame.ColorRange = Formats.ToFFmpeg(color.Range);
        frame.ColorPrimaries = Formats.ToFFmpeg(color.Primaries);
        frame.ColorTransfer = Formats.ToFFmpeg(color.Transfer);
    }
}
