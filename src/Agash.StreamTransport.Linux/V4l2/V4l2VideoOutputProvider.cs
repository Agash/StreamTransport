using System.Globalization;
using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Agash.StreamTransport.Linux.V4l2.V4l2Native;

namespace Agash.StreamTransport.Linux.V4l2;

/// <summary>
/// Received video as a webcam: frames written to a v4l2loopback device, which video calls, browsers and
/// OBS open as a camera. The output is named by the device's path or card name.
/// </summary>
/// <param name="loggerFactory">Where outputs log.</param>
public sealed class V4l2VideoOutputProvider(ILoggerFactory? loggerFactory = null)
    : IVideoOutputProvider
{
    private readonly ILoggerFactory _loggers = loggerFactory ?? NullLoggerFactory.Instance;

    /// <inheritdoc/>
    public string Name => "v4l2";

    /// <inheritdoc/>
    public ValueTask<IVideoOutput> CreateAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        string path =
            Find(name)
            ?? throw new InvalidOperationException(
                $"There is no V4L2 output device '{name}'; load v4l2loopback to make one."
            );
        return ValueTask.FromResult<IVideoOutput>(new V4l2VideoOutput(Name, path, _loggers));
    }

    // A device that takes video out, by path or card name.
    private static string? Find(string name)
    {
        if (name.StartsWith("/dev/", StringComparison.Ordinal))
        {
            return name;
        }

        foreach (string path in Directory.EnumerateFiles("/dev", "video*"))
        {
            int fd = Open(path, ORdWr | ONonBlock | OCloExec);
            if (fd < 0)
            {
                continue;
            }

            try
            {
                Capability capability = default;
                if (
                    Control(fd, QueryCap, ref capability) == 0
                    && (capability.Effective & CapVideoOutput) != 0
                    && string.Equals(
                        Text(Card(ref capability)),
                        name,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    return path;
                }
            }
            finally
            {
                _ = Close(fd);
            }
        }

        return null;
    }

    private static unsafe ReadOnlySpan<byte> Card(ref Capability capability)
    {
        fixed (byte* card = capability.Card)
        {
            return new ReadOnlySpan<byte>(card, 32).ToArray();
        }
    }
}

// Writes each frame whole: the device is set to the first frame's format and size, and set again when
// they change.
internal sealed partial class V4l2VideoOutput : IVideoOutput
{
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly int _fd;
    private VideoFormat? _format;
    private byte[] _picture = [];
    private bool _disposed;

    public V4l2VideoOutput(string provider, string path, ILoggerFactory loggers)
    {
        Provider = provider;
        Name = path;
        _logger = loggers.CreateLogger<V4l2VideoOutput>();
        _fd = Open(path, ORdWr | OCloExec);
        if (_fd < 0)
        {
            throw new IOException($"{path} could not be opened ({Marshal.GetLastPInvokeError()}).");
        }
    }

    public string Provider { get; }

    public string Name { get; }

    public VideoConstraints Constraints { get; } =
        VideoConstraints.Cpu(PixelFormat.Nv12, PixelFormat.Yuy2, PixelFormat.I420);

    public unsafe void OnFrame(in VideoFrame frame)
    {
        if (frame.Storage.Kind != VideoStorageKind.Cpu)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (frame.Format != _format)
            {
                SetFormat(frame.Format);
            }

            // Packed rows and planes back to back, as the device was told.
            int width = frame.Format.CodedSize.Width;
            int offset = 0;
            _ = frame.Storage.TryGetValue(out CpuImage image);
            for (int plane = 0; plane < frame.PlaneCount; plane++)
            {
                int rows = PlaneLayout.PlaneRows(
                    frame.Format.PixelFormat,
                    plane,
                    frame.Format.CodedSize.Height
                );
                int row = PlaneLayout
                    .Packed(frame.Format.PixelFormat, frame.Format.CodedSize)[plane]
                    .Stride;
                ReadOnlySpan<byte> source = frame.GetPlane(plane);
                int stride = image.Planes[plane].Stride;
                for (int y = 0; y < rows; y++)
                {
                    source.Slice(y * stride, row).CopyTo(_picture.AsSpan(offset));
                    offset += row;
                }
            }

            fixed (byte* data = _picture)
            {
                if (Write(_fd, data, (nuint)offset) < 0)
                {
                    LogWriteFailed(Name, Marshal.GetLastPInvokeError(), width);
                }
            }
        }
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
            _ = Close(_fd);
        }
    }

    private void SetFormat(VideoFormat format)
    {
        int size = PlaneLayout.PackedSize(format.PixelFormat, format.CodedSize);
        Format request = new() { Type = BufTypeVideoOutput };
        request.Pix.Width = (uint)format.CodedSize.Width;
        request.Pix.Height = (uint)format.CodedSize.Height;
        request.Pix.PixelFormat = V4l2Formats.FromMedia(format.PixelFormat)[0];
        request.Pix.Field = 1;
        request.Pix.BytesPerLine = (uint)
            PlaneLayout.Packed(format.PixelFormat, format.CodedSize)[0].Stride;
        request.Pix.SizeImage = (uint)size;
        if (Control(_fd, SetFmt, ref request) < 0)
        {
            throw new IOException(
                $"{Name} did not take {format.PixelFormat} at {format.CodedSize.Width}x{format.CodedSize.Height} ({Marshal.GetLastPInvokeError()})."
            );
        }

        _picture = new byte[size];
        _format = format;
        LogFormat(
            Name,
            format.PixelFormat,
            format.CodedSize.Width.ToString(CultureInfo.InvariantCulture)
                + "x"
                + format.CodedSize.Height.ToString(CultureInfo.InvariantCulture)
        );
    }

    [LoggerMessage(2570, LogLevel.Information, "Writing {Device} as {Format} {Size}.")]
    private partial void LogFormat(string device, PixelFormat format, string size);

    [LoggerMessage(
        2571,
        LogLevel.Warning,
        "A frame could not be written to {Device} ({Errno}, width {Width})."
    )]
    private partial void LogWriteFailed(string device, int errno, int width);
}
