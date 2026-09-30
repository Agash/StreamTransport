using System.Collections.Immutable;
using Agash.StreamTransport.Media;
using AVFoundation;
using Foundation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.MacOS.Audio;

/// <summary>
/// Audio from the default input device through Core Audio (AVAudioEngine). Audio arrives as 48 kHz
/// stereo float, converted from the device's format (a mono microphone is heard on both channels), and
/// each frame carries the time Core Audio reports for its first sample, on the media clock. One engine
/// serves every connected consumer and runs while any is connected.
/// </summary>
/// <remarks>
/// macOS asks the user before an application hears the microphone; until they allow it the device
/// delivers silence. An application states why in <c>NSMicrophoneUsageDescription</c>.
/// </remarks>
public sealed partial class CoreAudioSource : IAudioSource, IDisposable
{
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private ImmutableArray<IAudioFrameConsumer> _consumers = [];
    private Capture? _capture;

    /// <summary>A source on the default input device.</summary>
    /// <param name="loggerFactory">Where the source logs.</param>
    public CoreAudioSource(ILoggerFactory? loggerFactory = null) =>
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<CoreAudioSource>();

    /// <inheritdoc/>
    public AudioFormat Format => CoreAudioSink.Format;

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The input device refused to start.</exception>
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

    [LoggerMessage(
        2420,
        LogLevel.Information,
        "Capturing audio from a {Rate} Hz, {Channels}-channel input."
    )]
    private partial void LogCapturing(double rate, uint channels);

    [LoggerMessage(
        2421,
        LogLevel.Warning,
        "Converting captured audio failed ({Reason}); the audio was dropped."
    )]
    private partial void LogConversionFailed(string? reason);

    [LoggerMessage(2422, LogLevel.Warning, "A consumer failed to take captured audio.")]
    private partial void LogConsumerFailed(Exception exception);

    [LoggerMessage(
        2423,
        LogLevel.Error,
        "The input device changed and capturing did not start again on the new one."
    )]
    private partial void LogRestartFailed(Exception exception);

    private sealed class Connection(CoreAudioSource source, IAudioFrameConsumer consumer)
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

    // An engine tapping the input node, converting each buffer the tap hands over.
    private sealed class Capture : IAudioFrameConsumer, IDisposable
    {
        private readonly CoreAudioSource _source;
        private readonly AVAudioEngine _engine = new();
        private readonly CapturedAudio _conversion = new();
        private readonly NSObject _changes;
        private readonly Lock _gate = new();
        private bool _disposed;

        public Capture(CoreAudioSource source)
        {
            _source = source;
            _changes = NSNotificationCenter.DefaultCenter.AddObserver(
                AVAudioEngine.ConfigurationChangeNotification,
                _ => Restart(),
                _engine
            );
            Start();
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
            }

            NSNotificationCenter.DefaultCenter.RemoveObserver(_changes);
            _engine.InputNode.RemoveTapOnBus(0);
            _engine.Stop();
            _conversion.Dispose();
            _engine.Dispose();
        }

        // Taps the input in whatever format it delivers, which the tap requires; each buffer is
        // converted from its own format.
        private void Start()
        {
            using AVAudioFormat device = _engine.InputNode.GetBusInputFormat(0);
            if (device.SampleRate <= 0 || device.ChannelCount == 0)
            {
                throw new InvalidOperationException("The system has no audio input device.");
            }

            _engine.InputNode.InstallTapOnBus(0, 4096, null, Deliver);
            if (!_engine.StartAndReturnError(out NSError? error))
            {
                _engine.InputNode.RemoveTapOnBus(0);
                throw new InvalidOperationException(
                    $"The input device did not start: {error?.LocalizedDescription}"
                );
            }

            _source.LogCapturing(device.SampleRate, device.ChannelCount);
        }

        // The engine stops when the input device or its format changes; it starts again on the new one.
        private void Restart()
        {
            lock (_gate)
            {
                if (_disposed || _engine.Running)
                {
                    return;
                }

                try
                {
                    _engine.InputNode.RemoveTapOnBus(0);
                    Start();
                }
                catch (InvalidOperationException exception)
                {
                    _source.LogRestartFailed(exception);
                }
            }
        }

        // Runs on the tap's thread, one captured buffer at a time.
        private void Deliver(AVAudioPcmBuffer captured, AVAudioTime when)
        {
            using NSAutoreleasePool autoreleased = new();
            if (!_conversion.TryConvert(captured, when, this, out string? failure))
            {
                _source.LogConversionFailed(failure);
            }
        }

        // The converted audio, handed to every consumer.
        public void OnFrame(in AudioFrame frame)
        {
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
