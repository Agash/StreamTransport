using System.Collections.Immutable;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PipeWire.NET;
using PipeWire.NET.Media;
using AudioFormat = Agash.StreamTransport.Media.AudioFormat;
using AudioFrame = Agash.StreamTransport.Media.AudioFrame;
using PipeWireAudioFrame = PipeWire.NET.Media.AudioFrame;

namespace Agash.StreamTransport.Linux.PipeWire;

/// <summary>
/// Audio from a PipeWire node: the default input, a named device, or another application's output. The
/// graph converts it to 48 kHz stereo float. Each frame is stamped with when its first sample was
/// captured: the graph cycle it was queued in, less the delay the stream reports from the source. One
/// stream serves every connected consumer and runs while any is connected.
/// </summary>
public sealed partial class PipeWireAudioSource : IAudioSource, IDisposable
{
    private readonly PipeWireContext _context;
    private readonly PipeWireAudioOptions _options;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private ImmutableArray<IAudioFrameConsumer> _consumers = [];
    private PipeWireAudioCapture? _capture;

    /// <summary>A source on a PipeWire node.</summary>
    /// <param name="context">A started connection to the daemon.</param>
    /// <param name="options">What to capture; defaults when null.</param>
    /// <param name="loggerFactory">Where the source logs.</param>
    public PipeWireAudioSource(
        PipeWireContext context,
        PipeWireAudioOptions? options = null,
        ILoggerFactory? loggerFactory = null
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _options = options ?? new PipeWireAudioOptions();
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<PipeWireAudioSource>();
    }

    /// <summary>The format every source and sink here uses.</summary>
    public static AudioFormat Transport { get; } = new(SampleFormat.F32, 48_000, 2);

    /// <inheritdoc/>
    public AudioFormat Format => Transport;

    /// <summary>The capture's node id once the daemon has assigned one.</summary>
    public uint? NodeId => _capture?.NodeId;

    /// <inheritdoc/>
    public IDisposable Connect(IAudioFrameConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        lock (_gate)
        {
            _capture ??= Start();
            _consumers = _consumers.Add(consumer);
        }

        return new Connection(this, consumer);
    }

    /// <summary>Stops capturing and disconnects every consumer.</summary>
    public void Dispose()
    {
        PipeWireAudioCapture? capture;
        lock (_gate)
        {
            capture = _capture;
            _capture = null;
            _consumers = [];
        }

        capture?.Dispose();
    }

    private PipeWireAudioCapture Start()
    {
        PipeWireAudioCapture capture = new(_context, _options.NodeName);
        capture.FrameReady += Deliver;
        try
        {
            capture.Connect(
                _options.TargetNodeId ?? PipeWireAudioCapture.AnyNode,
                Transport.SampleRate,
                Transport.Channels,
                AudioSampleFormat.F32Le,
                _options.TargetObject,
                autoConnect: _options.AutoConnect
            );
        }
        catch
        {
            capture.Dispose();
            throw;
        }

        return capture;
    }

    private void Disconnect(IAudioFrameConsumer consumer)
    {
        PipeWireAudioCapture? stopped = null;
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

    // Runs on the PipeWire loop thread; the samples are the daemon's buffer, read in place.
    private void Deliver(PipeWireAudioCapture sender, PipeWireAudioFrame frame)
    {
        if (
            frame.Format != AudioSampleFormat.F32Le
            || frame.SampleRate != Transport.SampleRate
            || frame.Channels != Transport.Channels
        )
        {
            LogUnexpectedFormat(frame.Format, frame.SampleRate, frame.Channels);
            return;
        }

        MediaTime captured = frame.QueuedTimeNs is { } queued
            ? new MediaTime(queued - frame.DelayNs)
            : MediaClock.System.Now;
        AudioFrame audio = new(frame.Samples, Transport, MediaTimestamp.Captured(captured));
        foreach (IAudioFrameConsumer consumer in _consumers)
        {
            try
            {
                consumer.OnFrame(in audio);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // One consumer's failure does not stop the others or the stream.
                LogConsumerFailed(exception);
            }
        }
    }

    [LoggerMessage(
        2520,
        LogLevel.Warning,
        "PipeWire delivered {Format} at {Rate} Hz, {Channels} channels, where 48 kHz stereo float was asked for; the audio was dropped."
    )]
    private partial void LogUnexpectedFormat(AudioSampleFormat format, int rate, int channels);

    [LoggerMessage(2521, LogLevel.Warning, "A consumer failed to take PipeWire audio.")]
    private partial void LogConsumerFailed(Exception exception);

    private sealed class Connection(PipeWireAudioSource source, IAudioFrameConsumer consumer)
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
}
