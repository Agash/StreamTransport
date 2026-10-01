using Agash.StreamTransport.Media;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Codecs.FFmpeg;

/// <summary>
/// Makes CPU video processors on FFmpeg's swscale: pixel format, size and colour conversion between
/// frames in system memory. It ranks below every GPU processor, and does no alpha layout.
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
            || processing.Alpha != AlphaLayout.None
            || !processing.Output.Storages.Contains(VideoStorageKind.Cpu)
            || !FF.Scaler.IsSupportedInput(Formats.ToFFmpeg(input.PixelFormat))
        )
        {
            return null;
        }

        foreach (PixelFormat format in processing.Output.PixelFormats)
        {
            if (FF.Scaler.IsSupportedOutput(Formats.ToFFmpeg(format)))
            {
                VideoSize size = processing.Size ?? input.Size;
                return new VideoProcessorInfo(
                    Name,
                    IsHardwareAccelerated: false,
                    new VideoStreamDescription(VideoStorageKind.Cpu, format, size)
                );
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
            processing.Color
        );
}

// Converts one CPU frame at a time; each result is a fresh FFmpeg frame, so a retained result stays
// valid while the next is made.
internal sealed class FFmpegVideoProcessor(VideoProcessorInfo info, VideoColor? color)
    : IVideoProcessor,
        IVideoFrameRetainer
{
    private readonly FF.Scaler _scaler = new();
    private readonly FF.Frame _source = new();
    private readonly FF.Frame _output = new();
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
            // The scaler releases the previous result's buffer (a retained one keeps its reference) and
            // produces what the frame describes.
            _output.Width = output.Size.Width;
            _output.Height = output.Size.Height;
            _output.PixelFormat = Formats.ToFFmpeg(output.PixelFormat);
            SetColor(_output, target);
            _scaler.Scale(_source, _output);

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
        }
    }

    private static void SetColor(FF.Frame frame, VideoColor color)
    {
        frame.ColorSpace = Formats.ToFFmpeg(color.Matrix);
        frame.ColorRange = Formats.ToFFmpeg(color.Range);
        frame.ColorPrimaries = Formats.ToFFmpeg(color.Primaries);
        frame.ColorTransfer = Formats.ToFFmpeg(color.Transfer);
    }
}
