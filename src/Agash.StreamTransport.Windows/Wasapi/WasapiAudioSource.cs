using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32.Media.Audio;

namespace Agash.StreamTransport.Windows.Wasapi;

/// <summary>
/// Audio from a WASAPI endpoint: the default capture device, or what the default output plays
/// (loopback). Audio arrives as 48 kHz stereo float, converted by WASAPI from the device's format, and
/// each frame carries the time WASAPI reports for its first sample, on the media clock: when a
/// microphone captured it, or for loopback when it plays at the output device, which lies up to the
/// output buffer ahead. One capture thread serves every connected consumer and runs while any is
/// connected.
/// </summary>
public sealed partial class WasapiAudioSource : IAudioSource, IDisposable
{
    private readonly WasapiEndpoint _endpoint;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private ImmutableArray<IAudioFrameConsumer> _consumers = [];
    private Capture? _capture;

    /// <summary>A source on an endpoint.</summary>
    /// <param name="endpoint">The capture device, or an output to capture as it plays.</param>
    /// <param name="loggerFactory">Where the source logs.</param>
    public WasapiAudioSource(
        WasapiEndpoint endpoint = WasapiEndpoint.DefaultCapture,
        ILoggerFactory? loggerFactory = null
    )
    {
        if (endpoint == WasapiEndpoint.DefaultOutput)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endpoint),
                endpoint,
                "An output is captured through its loopback."
            );
        }

        _endpoint = endpoint;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WasapiAudioSource>();
    }

    /// <inheritdoc/>
    public AudioFormat Format => WasapiClient.Format;

    /// <inheritdoc/>
    public IDisposable Connect(IAudioFrameConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        lock (_gate)
        {
            _capture ??= new Capture(this);
            _consumers = _consumers.Add(consumer);
        }

        return new Connection(this, consumer);
    }

    /// <summary>Stops capturing and disconnects every consumer.</summary>
    public void Dispose()
    {
        Capture? capture;
        lock (_gate)
        {
            capture = _capture;
            _capture = null;
            _consumers = [];
        }

        capture?.Dispose();
    }

    private void Disconnect(IAudioFrameConsumer consumer)
    {
        Capture? stopped = null;
        lock (_gate)
        {
            _consumers = _consumers.Remove(consumer);
            if (_consumers.IsEmpty)
            {
                stopped = _capture;
                _capture = null;
            }
        }

        stopped?.Dispose();
    }

    [LoggerMessage(2320, LogLevel.Information, "Capturing audio from {Endpoint}.")]
    private partial void LogCapturing(WasapiEndpoint endpoint);

    [LoggerMessage(2321, LogLevel.Error, "Capturing audio from {Endpoint} stopped with an error.")]
    private partial void LogCaptureFailed(Exception exception, WasapiEndpoint endpoint);

    [LoggerMessage(2322, LogLevel.Warning, "A consumer failed to take captured audio.")]
    private partial void LogConsumerFailed(Exception exception);

    private sealed class Connection(WasapiAudioSource source, IAudioFrameConsumer consumer)
        : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                source.Disconnect(consumer);
            }
        }
    }

    // The capture thread: WASAPI's COM objects live and die on it.
    private sealed unsafe class Capture : IDisposable
    {
        private readonly WasapiAudioSource _source;
        private readonly ManualResetEvent _stop = new(false);
        private readonly Thread _thread;

        public Capture(WasapiAudioSource source)
        {
            _source = source;
            _thread = new Thread(Run) { IsBackground = true, Name = "WASAPI capture" };
            _thread.Start();
        }

        public void Dispose()
        {
            _ = _stop.Set();
            _thread.Join();
            _stop.Dispose();
        }

        private void Run()
        {
            try
            {
                using WasapiClient client = new(_source._endpoint);
                IAudioCaptureClient* capture = client.Service<IAudioCaptureClient>();
                try
                {
                    client.Start();
                    _source.LogCapturing(_source._endpoint);
                    WaitHandle[] waits = [client.Ready, _stop];
                    while (WaitHandle.WaitAny(waits) == 0)
                    {
                        Drain(capture);
                    }
                }
                finally
                {
                    _ = capture->Release();
                }
            }
            catch (COMException exception)
            {
                // The device went away or refused the stream; the consumers stop getting audio.
                _source.LogCaptureFailed(exception, _source._endpoint);
            }
        }

        // Hands on every packet the device has ready.
        private void Drain(IAudioCaptureClient* capture)
        {
            uint next;
            capture->GetNextPacketSize(&next);
            while (next != 0)
            {
                byte* data;
                uint frames;
                uint flags;
                ulong devicePosition;
                ulong qpcPosition;
                capture->GetBuffer(&data, &frames, &flags, &devicePosition, &qpcPosition);
                try
                {
                    Deliver(data, (int)frames, flags, qpcPosition);
                }
                finally
                {
                    capture->ReleaseBuffer(frames);
                }

                capture->GetNextPacketSize(&next);
            }
        }

        private void Deliver(byte* data, int frames, uint flags, ulong qpcPosition)
        {
            AudioFormat format = WasapiClient.Format;
            int bytes = frames * format.BytesPerFrame;
            bool silent = (flags & (uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT) != 0;
            byte[]? zeros = silent ? new byte[bytes] : null;
            ReadOnlySpan<byte> samples = silent ? zeros : new ReadOnlySpan<byte>(data, bytes);

            // WASAPI states the capture time in 100 ns units of the performance counter, which is the
            // system media clock's source.
            AudioFrame frame = new(
                samples,
                format,
                MediaTimestamp.Captured(new MediaTime((long)qpcPosition * 100))
            );
            foreach (IAudioFrameConsumer consumer in _source._consumers)
            {
                try
                {
                    consumer.OnFrame(in frame);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // One consumer's failure does not stop the others or the capture.
                    _source.LogConsumerFailed(exception);
                }
            }
        }
    }
}
