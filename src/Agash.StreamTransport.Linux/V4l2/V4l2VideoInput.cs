using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using static Agash.StreamTransport.Linux.V4l2.V4l2Native;

namespace Agash.StreamTransport.Linux.V4l2;

/// <summary>
/// An open V4L2 capture node. Streaming runs while a consumer is connected; each frame is a driver buffer,
/// handed to every consumer in the storage it takes and requeued once the last of them lets go. A consumer
/// that keeps a frame keeps its buffer, so a consumer that keeps frames for long starves the capture.
/// </summary>
internal sealed unsafe partial class V4l2VideoInput : IVideoInput, IVideoFrameRetainer
{
    private const int BufferCount = 6;
    private const uint FieldNone = 1;
    private const int MaxPlanes = 8;

    // GPUs import linear images with rows and planes at this alignment (AMD needs 256 bytes, the most of
    // the desktop vendors), so it is asked of the driver and required before buffers are lent as DMA-BUFs.
    private const int ImportAlignment = 256;

    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly int _fd;
    private readonly uint _type;
    private readonly VideoFormat _format;
    private readonly VideoColor _color;
    private readonly int[] _strides;
    private readonly int[] _planeOffsets;
    private readonly int[] _planeBuffers;
    private readonly Slot[] _slots;
    private readonly bool _canShare;
    private ImmutableArray<(IVideoFrameConsumer Consumer, VideoConstraints Constraints)> _consumers = [];
    private Thread? _thread;
    private int _wake = -1;
    private int _generation;
    private Slot? _delivering;
    private bool _disposed;

    public V4l2VideoInput(VideoInputInfo info, VideoInputMode mode, ILoggerFactory loggers)
    {
        Info = info;
        _logger = loggers.CreateLogger<V4l2VideoInput>();
        _fd = Open(info.Id, ORdWr | ONonBlock | OCloExec);
        if (_fd < 0)
        {
            throw new IOException($"{info.Id} could not be opened ({Marshal.GetLastPInvokeError()}).");
        }

        try
        {
            Capability capability = default;
            Check(Control(_fd, QueryCap, ref capability), "query its capabilities");
            _type = (capability.Effective & CapVideoCapture) != 0 ? BufTypeVideoCapture : BufTypeVideoCaptureMplane;
            Format format = SetFormat(mode);
            bool mplane = _type == BufTypeVideoCaptureMplane;
            uint width = mplane ? format.PixMp.Width : format.Pix.Width;
            uint height = mplane ? format.PixMp.Height : format.Pix.Height;
            _format = new VideoFormat(mode.PixelFormat, (int)width, (int)height);
            Mode = mode with { Size = new VideoSize((int)width, (int)height) };
            _color = mplane
                ? Color(format.PixMp.Colorspace, format.PixMp.YcbcrEncoding, format.PixMp.Quantization, format.PixMp.TransferFunction, mode.PixelFormat)
                : Color(format.Pix.Colorspace, format.Pix.YcbcrEncoding, format.Pix.Quantization, format.Pix.TransferFunction, mode.PixelFormat);
            (_strides, _planeOffsets, _planeBuffers) = Layout(format, mplane, mode.PixelFormat, (int)height);
            SetRate(mode.FrameRate);
            _slots = Allocate(_planeBuffers.Max() + 1, out bool exported);
            _canShare =
                exported
                && _strides.All(static s => s % ImportAlignment == 0)
                && _planeOffsets.All(static o => o % ImportAlignment == 0);
            LogOpened(
                info.Name,
                Mode,
                FourCcName(mplane ? format.PixMp.PixelFormat : format.Pix.PixelFormat),
                _canShare
            );
        }
        catch
        {
            _ = Close(_fd);
            throw;
        }
    }

    public VideoInputInfo Info { get; }

    public VideoInputMode? Mode { get; }

    public IDisposable Connect(IVideoFrameConsumer consumer, VideoConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(constraints);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _consumers = _consumers.Add((consumer, constraints));
            if (_thread is null)
            {
                Start();
            }
        }

        return new Connection(this, consumer);
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
            _consumers = [];
        }

        Stop();
        foreach (Slot slot in _slots)
        {
            slot.Unmap();
        }

        _ = Close(_fd);
    }

    VideoFrameLease IVideoFrameRetainer.Retain(in VideoFrame frame)
    {
        Slot slot = _delivering ?? throw new InvalidOperationException("Frames are kept while they are delivered.");
        _ = Interlocked.Increment(ref slot.Holds);
        return new V4l2FrameLease(this, slot, in frame);
    }

    // Queues every buffer and starts streaming and the thread that dequeues.
    private void Start()
    {
        _wake = EventFd(0, OCloExec);
        foreach (Slot slot in _slots)
        {
            slot.Holds = 0;
            Queue(slot);
        }

        int type = (int)_type;
        Check(Control(_fd, StreamOn, ref type), "start streaming");
        int generation = ++_generation;
        _thread = new Thread(() => Run(generation)) { IsBackground = true, Name = $"V4L2 {Info.Name}" };
        _thread.Start();
    }

    private void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _thread = null;
            _generation++;
        }

        if (thread is null)
        {
            return;
        }

        ulong one = 1;
        _ = Write(_wake, &one, sizeof(ulong));
        thread.Join();
        int type = (int)_type;
        _ = Control(_fd, StreamOff, ref type);
        _ = Close(_wake);
        _wake = -1;
    }

    private void Run(int generation)
    {
        PollFd* fds = stackalloc PollFd[2];
        fds[0] = new PollFd { Fd = _fd, Events = PollIn };
        fds[1] = new PollFd { Fd = _wake, Events = PollIn };
        Plane* planes = stackalloc Plane[MaxPlanes];
        while (true)
        {
            if (Poll(fds, 2, -1) < 0)
            {
                if (Marshal.GetLastPInvokeError() == EIntr)
                {
                    continue;
                }

                LogFailed("poll", Marshal.GetLastPInvokeError());
                return;
            }

            if ((fds[1].ReturnedEvents & PollIn) != 0)
            {
                return;
            }

            V4l2Buffer buffer = new() { Type = _type, Memory = MemoryMmap, Planes = planes, Length = MaxPlanes };
            if (Control(_fd, DqBuf, ref buffer) < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno == EAgain)
                {
                    continue;
                }

                LogFailed("dequeue a buffer", errno);
                return;
            }

            Slot slot = _slots[buffer.Index];
            slot.Holds = 1;
            if ((buffer.Flags & BufFlagError) == 0)
            {
                Deliver(slot, in buffer);
            }

            Release(slot, generation);
        }
    }

    private void Deliver(Slot slot, in V4l2Buffer buffer)
    {
        MediaTimestamp timestamp =
            (buffer.Flags & BufFlagTimestampMask) == BufFlagTimestampMonotonic
                ? MediaTimestamp.Captured(new MediaTime((buffer.TimestampSeconds * 1_000_000_000) + (buffer.TimestampMicroseconds * 1000)))
                : MediaTimestamp.Observed(MediaClock.System.Now);
        _delivering = slot;
        try
        {
            foreach ((IVideoFrameConsumer consumer, VideoConstraints constraints) in _consumers)
            {
                try
                {
                    if (
                        _canShare
                        && constraints.Storages.Contains(VideoStorageKind.DmaBuf)
                        && constraints.Device is { Kind: GpuIdentityKind.DrmDevice } device
                    )
                    {
                        DeliverShared(consumer, slot, timestamp, device);
                    }
                    else
                    {
                        DeliverMapped(consumer, slot, timestamp);
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // One consumer's failure does not stop the others or the capture.
                    LogConsumerFailed(exception);
                }
            }
        }
        finally
        {
            _delivering = null;
        }
    }

    private void DeliverMapped(IVideoFrameConsumer consumer, Slot slot, MediaTimestamp timestamp)
    {
        VideoFrame frame = new(
            _format,
            timestamp,
            Plane(slot, 0),
            _strides[0],
            _strides.Length > 1 ? Plane(slot, 1) : default,
            _strides.Length > 1 ? _strides[1] : 0,
            _strides.Length > 2 ? Plane(slot, 2) : default,
            _strides.Length > 2 ? _strides[2] : 0,
            color: _color,
            retainer: this
        );
        consumer.OnFrame(in frame);
    }

    private void DeliverShared(IVideoFrameConsumer consumer, Slot slot, MediaTimestamp timestamp, GpuIdentity device)
    {
        Span<DmaBufPlane> planes = stackalloc DmaBufPlane[_strides.Length];
        for (int i = 0; i < planes.Length; i++)
        {
            planes[i] = new DmaBufPlane(slot.Exported[_planeBuffers[i]], _planeOffsets[i], _strides[i]);
        }

        VideoFrame frame = new(
            new VideoStorage(new DmaBufImage(planes, V4l2Formats.DrmFourCc(_format.PixelFormat), 0, device)),
            _format,
            timestamp,
            color: _color,
            retainer: this
        );
        consumer.OnFrame(in frame);
    }

    internal ReadOnlySpan<byte> Plane(Slot slot, int plane)
    {
        int buffer = _planeBuffers[plane];
        int start = _planeOffsets[plane];
        int rows = PlaneLayout.PlaneRows(_format.PixelFormat, plane, _format.CodedSize.Height);
        int length = Math.Min(rows * _strides[plane], (int)slot.Lengths[buffer] - start);
        return new ReadOnlySpan<byte>((byte*)slot.Maps[buffer] + start, length);
    }

    // Requeues a buffer once its last holder lets go, unless streaming stopped meanwhile.
    internal void Release(Slot slot, int generation = -1)
    {
        if (Interlocked.Decrement(ref slot.Holds) != 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_thread is not null && (generation < 0 || generation == _generation) && !_disposed)
            {
                Queue(slot);
            }
        }
    }

    private void Queue(Slot slot)
    {
        Plane* planes = stackalloc Plane[MaxPlanes];
        V4l2Buffer buffer = new() { Index = slot.Index, Type = _type, Memory = MemoryMmap, Planes = planes, Length = MaxPlanes };
        if (Control(_fd, QBuf, ref buffer) < 0)
        {
            LogFailed("queue a buffer", Marshal.GetLastPInvokeError());
        }
    }

    private Format SetFormat(VideoInputMode mode)
    {
        foreach (uint fourcc in V4l2Formats.FromMedia(mode.PixelFormat))
        {
            Format format = new() { Type = _type };
            if (_type == BufTypeVideoCaptureMplane)
            {
                format.PixMp.Width = (uint)mode.Size.Width;
                format.PixMp.Height = (uint)mode.Size.Height;
                format.PixMp.PixelFormat = fourcc;
                format.PixMp.Field = FieldNone;
                format.PixMp.Plane0.BytesPerLine = AlignedStride(mode);
            }
            else
            {
                format.Pix.Width = (uint)mode.Size.Width;
                format.Pix.Height = (uint)mode.Size.Height;
                format.Pix.PixelFormat = fourcc;
                format.Pix.Field = FieldNone;
                format.Pix.BytesPerLine = AlignedStride(mode);
            }

            if (
                Control(_fd, SetFmt, ref format) == 0
                && (_type == BufTypeVideoCaptureMplane ? format.PixMp.PixelFormat : format.Pix.PixelFormat) == fourcc
            )
            {
                return format;
            }
        }

        throw new NotSupportedException($"{Info.Name} did not take {mode}.");
    }

    // The first plane's row pitch rounded up to what GPUs import; drivers that cannot pad keep their own.
    private static uint AlignedStride(VideoInputMode mode)
    {
        int bytes = mode.PixelFormat switch
        {
            PixelFormat.Yuy2 or PixelFormat.Uyvy => 2,
            PixelFormat.Bgra or PixelFormat.Rgba => 4,
            _ => 1,
        };
        int stride = mode.Size.Width * bytes;
        return (uint)((stride + ImportAlignment - 1) / ImportAlignment * ImportAlignment);
    }

    // Asks for the frame interval; a device that cannot change it keeps its own.
    private void SetRate(double rate)
    {
        StreamParm parm = new()
        {
            Type = _type,
            TimePerFrameNumerator = 1000,
            TimePerFrameDenominator = (uint)Math.Round(rate * 1000),
        };
        if (Control(_fd, SetParm, ref parm) < 0)
        {
            LogRateKept(Info.Name, rate);
        }
    }

    // The stride, offset and memory plane of each picture plane.
    private static (int[] Strides, int[] Offsets, int[] Buffers) Layout(
        in Format format,
        bool mplane,
        PixelFormat pixelFormat,
        int height
    )
    {
        int planes = PlaneLayout.PlaneCount(pixelFormat);
        int[] strides = new int[planes];
        int[] offsets = new int[planes];
        int[] buffers = new int[planes];
        if (mplane && format.PixMp.PlaneCount > 1)
        {
            // One memory plane per picture plane (NV12M and kin).
            ReadOnlySpan<PlanePixFormat> planeFormats = MemoryMarshal.CreateReadOnlySpan(
                in format.PixMp.Plane0,
                MaxPlanes
            );
            for (int i = 0; i < planes; i++)
            {
                strides[i] = (int)planeFormats[i].BytesPerLine;
                buffers[i] = i;
            }

            return (strides, offsets, buffers);
        }

        int stride = (int)(mplane ? format.PixMp.Plane0.BytesPerLine : format.Pix.BytesPerLine);
        PlaneLayout layout = pixelFormat switch
        {
            PixelFormat.Nv12 => new PlaneLayout([new(0, stride), new(stride * height, stride)]),
            PixelFormat.I420 => new PlaneLayout([
                new(0, stride),
                new(stride * height, stride / 2),
                new((stride * height) + ((stride / 2) * ((height + 1) / 2)), stride / 2),
            ]),
            _ => new PlaneLayout([new(0, stride)]),
        };
        for (int i = 0; i < planes; i++)
        {
            strides[i] = layout[i].Stride;
            offsets[i] = layout[i].Offset;
        }

        return (strides, offsets, buffers);
    }

    private Slot[] Allocate(int memoryPlanes, out bool canShare)
    {
        RequestBuffers request = new() { Count = BufferCount, Type = _type, Memory = MemoryMmap };
        Check(Control(_fd, ReqBufs, ref request), "allocate buffers");
        if (request.Count == 0)
        {
            throw new IOException($"{Info.Name} allocated no buffers.");
        }

        canShare = true;
        var slots = new Slot[request.Count];
        Plane* planes = stackalloc Plane[MaxPlanes];
        for (uint index = 0; index < request.Count; index++)
        {
            V4l2Buffer buffer = new() { Index = index, Type = _type, Memory = MemoryMmap, Planes = planes, Length = MaxPlanes };
            Check(Control(_fd, QueryBuf, ref buffer), "describe a buffer");
            Slot slot = new(index, memoryPlanes);
            slots[index] = slot;
            for (int plane = 0; plane < memoryPlanes; plane++)
            {
                bool mplane = _type == BufTypeVideoCaptureMplane;
                uint length = mplane ? planes[plane].Length : buffer.Length;
                long offset = mplane ? planes[plane].MemOffset : buffer.Offset;
                nint map = Mmap(0, length, ProtRead, MapShared, _fd, offset);
                if (map == -1)
                {
                    throw new IOException($"{Info.Name} buffer {index} could not be mapped ({Marshal.GetLastPInvokeError()}).");
                }

                slot.Maps[plane] = map;
                slot.Lengths[plane] = length;
                ExportBuffer export = new() { Type = _type, Index = index, Plane = (uint)plane, Flags = (uint)OCloExec };
                if (Control(_fd, ExpBuf, ref export) == 0)
                {
                    slot.Exported[plane] = export.Fd;
                }
                else
                {
                    canShare = false;
                }
            }
        }

        return slots;
    }

    // The colour V4L2 describes, with its defaults for what it leaves unsaid.
    private static VideoColor Color(uint colorspace, uint encoding, uint quantization, uint transfer, PixelFormat format)
    {
        bool rgb = format is PixelFormat.Bgra or PixelFormat.Rgba;
        // V4L2_COLORSPACE_: SMPTE170M 1, REC709 3, SRGB 8, BT2020 10.
        ColorPrimaries primaries = colorspace switch
        {
            1 => ColorPrimaries.Bt601,
            10 => ColorPrimaries.Bt2020,
            _ => ColorPrimaries.Bt709,
        };
        // V4L2_YCBCR_ENC_: 601 1, 709 2, BT2020 6; the default follows the colorspace (sRGB cameras use 601).
        ColorMatrix matrix = rgb ? ColorMatrix.Identity
            : encoding switch
            {
                1 => ColorMatrix.Bt601,
                2 => ColorMatrix.Bt709,
                6 => ColorMatrix.Bt2020,
                _ => colorspace switch
                {
                    3 => ColorMatrix.Bt709,
                    10 => ColorMatrix.Bt2020,
                    _ => ColorMatrix.Bt601,
                },
            };
        // V4L2_QUANTIZATION_: FULL_RANGE 1, LIM_RANGE 2; the default is limited for Y'CbCr, full for RGB.
        ColorRange range = quantization switch
        {
            1 => ColorRange.Full,
            2 => ColorRange.Limited,
            _ => rgb ? ColorRange.Full : ColorRange.Limited,
        };
        // V4L2_XFER_FUNC_: 709 1, SRGB 2; the default follows the colorspace.
        TransferFunction function = transfer switch
        {
            1 => TransferFunction.Bt709,
            2 => TransferFunction.Srgb,
            _ => colorspace == 8 || rgb ? TransferFunction.Srgb : TransferFunction.Bt709,
        };
        return new VideoColor(matrix, range, primaries, function);
    }

    private void Check(int result, string what)
    {
        if (result < 0)
        {
            throw new IOException($"{Info.Name} could not {what} ({Marshal.GetLastPInvokeError()}).");
        }
    }

    private void Disconnect(IVideoFrameConsumer consumer)
    {
        bool last;
        lock (_gate)
        {
            _consumers = _consumers.RemoveAll(c => ReferenceEquals(c.Consumer, consumer));
            last = _consumers.IsEmpty;
        }

        if (last)
        {
            Stop();
        }
    }

    [LoggerMessage(2565, LogLevel.Information, "Opened {Device} in {Mode} ({FourCc}); sharing DMA-BUFs: {Shared}.")]
    private partial void LogOpened(string device, VideoInputMode? mode, string fourCc, bool shared);

    [LoggerMessage(2566, LogLevel.Error, "V4L2 capture could not {What} ({Errno}); it stops.")]
    private partial void LogFailed(string what, int errno);

    [LoggerMessage(2567, LogLevel.Warning, "A consumer failed to take a V4L2 frame.")]
    private partial void LogConsumerFailed(Exception exception);

    [LoggerMessage(2568, LogLevel.Information, "{Device} keeps its own frame rate instead of {Rate}.")]
    private partial void LogRateKept(string device, double rate);

    // One driver buffer: its mappings and exported DMA-BUFs per memory plane, and who holds it.
    internal sealed class Slot(uint index, int planes)
    {
        public uint Index { get; } = index;

        public nint[] Maps { get; } = new nint[planes];

        public nuint[] Lengths { get; } = new nuint[planes];

        public int[] Exported { get; } = [.. Enumerable.Repeat(-1, planes)];

        public int Holds;

        public void Unmap()
        {
            for (int i = 0; i < Maps.Length; i++)
            {
                if (Maps[i] != 0)
                {
                    _ = Munmap(Maps[i], Lengths[i]);
                    Maps[i] = 0;
                }

                if (Exported[i] >= 0)
                {
                    _ = Close(Exported[i]);
                    Exported[i] = -1;
                }
            }
        }
    }

    private sealed class Connection(V4l2VideoInput input, IVideoFrameConsumer consumer) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                input.Disconnect(consumer);
            }
        }
    }
}

// A kept frame: its driver buffer stays dequeued until the lease and every lease shared from it go.
internal sealed class V4l2FrameLease : VideoFrameLease
{
    private readonly V4l2VideoInput _input;
    private readonly V4l2VideoInput.Slot _slot;

    public V4l2FrameLease(V4l2VideoInput input, V4l2VideoInput.Slot slot, in VideoFrame frame)
        : base(frame.Storage, frame.Format, frame.Timestamp, frame.Color, frame.Orientation, frame.Duration)
    {
        _input = input;
        _slot = slot;
    }

    protected override ReadOnlySpan<byte> GetPlane(int index) => _input.Plane(_slot, index);

    protected override void Release() => _input.Release(_slot);
}
