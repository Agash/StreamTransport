using Agash.StreamTransport.Media;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Codecs.FFmpeg;

// Moving pictures between the Media frame model and FFmpeg frames.
internal static class FFmpegFrames
{
    // A borrowed view of an FFmpeg frame's planes as a CPU frame.
    public static unsafe VideoFrame View(
        FF.Frame frame,
        VideoFormat format,
        MediaTimestamp timestamp,
        VideoColor color,
        IVideoFrameRetainer? retainer
    )
    {
        var native = frame.NativePointer;
        int count = PlaneLayout.PlaneCount(format.PixelFormat);
        ReadOnlySpan<byte> Plane(int index) =>
            index < count
                ? new ReadOnlySpan<byte>(
                    native->data[index],
                    native->linesize[index]
                        * PlaneLayout.PlaneRows(format.PixelFormat, index, frame.Height)
                )
                : default;

        return new VideoFrame(
            format,
            timestamp,
            Plane(0),
            native->linesize[0],
            Plane(1),
            count > 1 ? native->linesize[1] : 0,
            Plane(2),
            count > 2 ? native->linesize[2] : 0,
            Plane(3),
            count > 3 ? native->linesize[3] : 0,
            color,
            retainer: retainer
        );
    }

    // Copies a CPU frame's planes into an FFmpeg frame allocated for them.
    public static void CopyFrom(in VideoFrame frame, FF.Frame destination)
    {
        if (!frame.Storage.TryGetValue(out CpuImage image))
        {
            throw new ArgumentException("The frame is not in CPU memory.", nameof(frame));
        }

        VideoSize size = new(frame.Format.VisibleRect.Width, frame.Format.VisibleRect.Height);
        destination.AllocateVideo(
            size.Width,
            size.Height,
            Formats.ToFFmpeg(frame.Format.PixelFormat)
        );
        for (int plane = 0; plane < image.Planes.Count; plane++)
        {
            ReadOnlySpan<byte> source = frame.GetPlane(plane);
            int stride = image.Planes[plane].Stride;
            FF.ImagePlane target = destination.GetWritablePlane(plane);
            int rowLength = target.RowLength;
            int top = RowOffset(frame.Format.PixelFormat, plane, frame.Format.VisibleRect.Y);
            int left = ByteOffset(frame.Format.PixelFormat, plane, frame.Format.VisibleRect.X);
            for (int row = 0; row < target.Height; row++)
            {
                source.Slice(((top + row) * stride) + left, rowLength).CopyTo(target.GetRow(row));
            }
        }
    }

    // The first row of a plane for a visible rectangle starting at luma row y.
    private static int RowOffset(PixelFormat format, int plane, int y) =>
        PlaneLayout.IsChroma(format, plane) ? y / 2 : y;

    // The first byte of a row for a visible rectangle starting at luma column x.
    private static int ByteOffset(PixelFormat format, int plane, int x) =>
        format switch
        {
            PixelFormat.Nv12 => x,
            PixelFormat.P010 => 2 * x,
            PixelFormat.I420 or PixelFormat.Yuva420 => PlaneLayout.IsChroma(format, plane)
                ? x / 2
                : x,
            _ => 4 * x,
        };
}

// A frame kept by reference: the FFmpeg frame, and with it its buffers or surface, stays alive
// until the lease is disposed.
internal sealed class FFmpegFrameLease : VideoFrameLease, IVideoFrameRetainer
{
    private readonly FF.Frame _frame;

    public FFmpegFrameLease(in VideoFrame frame, FF.Frame reference)
        : base(
            frame.Storage,
            frame.Format,
            frame.Timestamp,
            frame.Color,
            frame.Orientation,
            frame.Duration
        )
    {
        _frame = reference;
    }

    protected override IVideoFrameRetainer KeepAgain => this;

    public VideoFrameLease Retain(in VideoFrame frame) =>
        new FFmpegFrameLease(in frame, _frame.Clone());

    protected override unsafe ReadOnlySpan<byte> GetPlane(int index)
    {
        var native = _frame.NativePointer;
        return new ReadOnlySpan<byte>(
            native->data[index],
            native->linesize[index]
                * PlaneLayout.PlaneRows(Format.PixelFormat, index, _frame.Height)
        );
    }

    protected override void Release() => _frame.Dispose();
}
