using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace Agash.StreamTransport.Windows.MediaFoundation;

/// <summary>
/// An open Media Foundation camera. A thread reads samples while a consumer is connected and hands each
/// to every consumer as a CPU frame over the sample's own memory. A sample's time is the camera's capture
/// time on the system's performance counter, which is the media clock on Windows.
/// </summary>
internal sealed unsafe partial class MediaFoundationVideoInput : IVideoInput
{
    // How far a sample time may stand from now and still be taken for a capture time on the media clock.
    private static readonly TimeSpan PlausibleSkew = TimeSpan.FromSeconds(2);

    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly IMFMediaSource* _source;
    private readonly IMFSourceReader* _reader;
    private readonly VideoFormat _format;
    private readonly VideoColor _color;
    private ImmutableArray<IVideoFrameConsumer> _consumers = [];
    private Thread? _thread;
    private volatile bool _stopping;
    private bool _disposed;

    public MediaFoundationVideoInput(VideoInputInfo info, VideoInputMode mode, ILoggerFactory loggers)
    {
        Info = info;
        Mode = mode;
        _logger = loggers.CreateLogger<MediaFoundationVideoInput>();
        MediaFoundationPlatform.Start();
        _source = CreateSource(info.Id);
        try
        {
            _reader = MediaFoundationPlatform.Reader(_source);
            IMFMediaType* type = null;
            foreach ((VideoInputMode native, uint index) in MediaFoundationPlatform.NativeTypes(_reader))
            {
                if (native == mode)
                {
                    _reader->GetNativeMediaType(MediaFoundationPlatform.FirstVideoStream, index, &type);
                    break;
                }
            }

            if (type is null)
            {
                throw new NotSupportedException($"{info.Name} no longer offers {mode}.");
            }

            try
            {
                _reader->SetCurrentMediaType(MediaFoundationPlatform.FirstVideoStream, null, type);
                _color = MediaFoundationPlatform.Color(type, mode.PixelFormat);
            }
            finally
            {
                _ = type->Release();
            }

            _format = new VideoFormat(mode.PixelFormat, mode.Size.Width, mode.Size.Height);
            LogOpened(info.Name, mode);
        }
        catch
        {
            if (_reader is not null)
            {
                _ = _reader->Release();
            }

            _source->Shutdown();
            _ = _source->Release();
            throw;
        }
    }

    public VideoInputInfo Info { get; }

    public VideoInputMode? Mode { get; }

    public IDisposable Connect(IVideoFrameConsumer consumer, VideoConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _consumers = _consumers.Add(consumer);
            if (_thread is null)
            {
                _stopping = false;
                _thread = new Thread(Run) { IsBackground = true, Name = $"Media Foundation {Info.Name}" };
                _thread.Start();
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
        _ = _reader->Release();
        _source->Shutdown();
        _ = _source->Release();
    }

    private static IMFMediaSource* CreateSource(string symbolicLink)
    {
        IMFAttributes* attributes;
        Win32.MFCreateAttributes(&attributes, 2).ThrowOnFailure();
        try
        {
            Guid sourceType = Win32.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE;
            Guid videoCapture = Win32.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID;
            Guid linkKey = Win32.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK;
            attributes->SetGUID(&sourceType, &videoCapture);
            fixed (char* link = symbolicLink)
            {
                attributes->SetString(&linkKey, link);
            }

            IMFMediaSource* source;
            Win32.MFCreateDeviceSource(attributes, &source).ThrowOnFailure();
            return source;
        }
        finally
        {
            _ = attributes->Release();
        }
    }

    private void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _thread = null;
            _stopping = true;
        }

        // A blocked read returns with the next sample, a frame period away.
        thread?.Join();
    }

    private void Run()
    {
        Win32.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED).ThrowOnFailure();
        try
        {
            while (!_stopping)
            {
                uint flags;
                long time;
                IMFSample* sample = null;
                try
                {
                    _reader->ReadSample(MediaFoundationPlatform.FirstVideoStream, 0, null, &flags, &time, &sample);
                }
                catch (COMException exception)
                {
                    LogReadFailed(exception, Info.Name);
                    return;
                }

                if (sample is null)
                {
                    // A stream tick or a format change carries no picture.
                    continue;
                }

                try
                {
                    Deliver(sample, time);
                }
                finally
                {
                    _ = sample->Release();
                }
            }
        }
        finally
        {
            Win32.CoUninitialize();
        }
    }

    private void Deliver(IMFSample* sample, long time)
    {
        IMFMediaBuffer* buffer;
        sample->ConvertToContiguousBuffer(&buffer);
        try
        {
            Guid twoDimensional = typeof(IMF2DBuffer).GUID;
            void* planar;
            bool locked2D = buffer->QueryInterface(&twoDimensional, &planar).Succeeded;
            byte* scan0;
            int pitch;
            uint length;
            if (locked2D)
            {
                ((IMF2DBuffer*)planar)->Lock2D(&scan0, &pitch);
                ((IMF2DBuffer*)planar)->GetContiguousLength(&length);
            }
            else
            {
                uint max;
                buffer->Lock(&scan0, &max, &length);
                pitch = Stride(_format);
            }

            try
            {
                Hand(scan0, pitch, (int)length, Timestamp(time));
            }
            finally
            {
                if (locked2D)
                {
                    ((IMF2DBuffer*)planar)->Unlock2D();
                    _ = ((IUnknown*)planar)->Release();
                }
                else
                {
                    buffer->Unlock();
                }
            }
        }
        finally
        {
            _ = buffer->Release();
        }
    }

    private void Hand(byte* scan0, int pitch, int length, MediaTimestamp timestamp)
    {
        int height = _format.CodedSize.Height;
        ReadOnlySpan<byte> all = new(scan0, length);
        VideoFrame frame = _format.PixelFormat switch
        {
            PixelFormat.Nv12 => new VideoFrame(
                _format,
                timestamp,
                all[..(pitch * height)],
                pitch,
                all[(pitch * height)..],
                pitch,
                color: _color
            ),
            PixelFormat.I420 => new VideoFrame(
                _format,
                timestamp,
                all[..(pitch * height)],
                pitch,
                all.Slice(pitch * height, pitch / 2 * (height / 2)),
                pitch / 2,
                all[((pitch * height) + (pitch / 2 * (height / 2)))..],
                pitch / 2,
                color: _color
            ),
            _ => new VideoFrame(_format, timestamp, all, pitch, color: _color),
        };
        foreach (IVideoFrameConsumer consumer in _consumers)
        {
            try
            {
                consumer.OnFrame(in frame);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // One consumer's failure does not stop the others or the capture.
                LogConsumerFailed(exception);
            }
        }
    }

    // A sample time is 100 ns ticks of the performance counter, as the media clock counts.
    private static MediaTimestamp Timestamp(long hundredNanoseconds)
    {
        MediaTime now = MediaClock.System.Now;
        var captured = new MediaTime(hundredNanoseconds * 100);
        return (now - captured).Duration() < PlausibleSkew
            ? MediaTimestamp.Captured(captured)
            : MediaTimestamp.Observed(now);
    }

    private static int Stride(VideoFormat format) =>
        format.PixelFormat switch
        {
            PixelFormat.Yuy2 or PixelFormat.Uyvy => format.CodedSize.Width * 2,
            PixelFormat.Bgra or PixelFormat.Rgba => format.CodedSize.Width * 4,
            _ => format.CodedSize.Width,
        };

    private void Disconnect(IVideoFrameConsumer consumer)
    {
        bool last;
        lock (_gate)
        {
            _consumers = _consumers.Remove(consumer);
            last = _consumers.IsEmpty;
        }

        if (last)
        {
            Stop();
        }
    }

    [LoggerMessage(2585, LogLevel.Information, "Opened {Camera} in {Mode}.")]
    private partial void LogOpened(string camera, VideoInputMode mode);

    [LoggerMessage(2586, LogLevel.Error, "Reading {Camera} failed; its capture stops.")]
    private partial void LogReadFailed(Exception exception, string camera);

    [LoggerMessage(2587, LogLevel.Warning, "A consumer failed to take a Media Foundation frame.")]
    private partial void LogConsumerFailed(Exception exception);

    private sealed class Connection(MediaFoundationVideoInput input, IVideoFrameConsumer consumer) : IDisposable
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
