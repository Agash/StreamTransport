using System.Buffers;
using System.Threading.Channels;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport;

/// <summary>
/// An encoded transform that records the video passing through it in the codec's elementary stream
/// format: Annex B for H.264 and H.265, low-overhead OBUs for AV1. FFmpeg reads these as they are
/// (<c>ffprobe</c>, <c>ffplay</c>, and its <c>h264</c>, <c>hevc</c> and <c>obu</c> demuxers), so a
/// recording replays the exact stream a session sent or received, for a decoder or for analysis.
/// </summary>
/// <remarks>
/// Every frame passes on unchanged before it is copied, so the decoder or packetizer never waits for the
/// recording. Copies go into pooled buffers and are written by a task of the recorder's own, so recording
/// adds no I/O to the media path; frames the writer is more than its capacity behind on are left out of
/// the recording and counted in <see cref="FramesDropped"/>. A recorder on several sessions (a publisher
/// serving several subscribers) records their frames as they come, one after another, so a recording
/// meant for a decoder is made on one session.
/// </remarks>
public sealed partial class EncodedVideoRecorder : IEncodedVideoTransform, IAsyncDisposable
{
    private readonly Stream _output;
    private readonly bool _ownsOutput;
    private readonly Channel<(byte[] Buffer, int Length)> _pending;
    private readonly ILogger _logger;
    private readonly Task _writer;
    private long _recorded;
    private long _dropped;

    /// <summary>A recorder writing to a stream.</summary>
    /// <param name="output">Where the elementary stream goes.</param>
    /// <param name="ownsOutput">Whether disposing the recorder disposes <paramref name="output"/>.</param>
    /// <param name="capacity">Frames waiting to be written before more are left out.</param>
    /// <param name="logger">Logging; none when null.</param>
    public EncodedVideoRecorder(
        Stream output,
        bool ownsOutput = false,
        int capacity = 256,
        ILogger<EncodedVideoRecorder>? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _output = output;
        _ownsOutput = ownsOutput;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _pending = Channel.CreateBounded<(byte[], int)>(
            new BoundedChannelOptions(capacity) { SingleReader = true }
        );
        _writer = WriteAsync();
    }

    /// <summary>A recorder writing to a new file.</summary>
    /// <param name="path">The file; an existing one is replaced.</param>
    /// <param name="logger">Logging; none when null.</param>
    /// <returns>The recorder, which owns the file.</returns>
    public static EncodedVideoRecorder Create(
        string path,
        ILogger<EncodedVideoRecorder>? logger = null
    ) =>
        new(
            new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 1 << 16,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            ),
            ownsOutput: true,
            logger: logger
        );

    /// <summary>Frames written so far.</summary>
    public long FramesRecorded => Interlocked.Read(ref _recorded);

    /// <summary>Frames left out because the writer was behind.</summary>
    public long FramesDropped => Interlocked.Read(ref _dropped);

    /// <inheritdoc/>
    public void Transform(in EncodedVideoFrame frame, IEncodedVideoConsumer next)
    {
        ArgumentNullException.ThrowIfNull(next);

        // On first, then copied: the frame is borrowed for the whole call, and the decoder or packetizer
        // does not wait for the copy.
        next.OnEncoded(in frame);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(frame.Data.Length);
        frame.Data.CopyTo(buffer);
        if (!_pending.Writer.TryWrite((buffer, frame.Data.Length)))
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (Interlocked.Increment(ref _dropped) == 1)
            {
                LogBehind();
            }
        }
    }

    /// <summary>Writes what is still waiting, then closes the recording.</summary>
    /// <returns>When it is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        _ = _pending.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
        if (_ownsOutput)
        {
            await _output.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task WriteAsync()
    {
        // Off the caller's thread from the start: the constructor returns before any write.
        await Task.Yield();
        try
        {
            await foreach (
                (byte[] buffer, int length) in _pending.Reader.ReadAllAsync().ConfigureAwait(false)
            )
            {
                try
                {
                    await _output.WriteAsync(buffer.AsMemory(0, length)).ConfigureAwait(false);
                    Interlocked.Increment(ref _recorded);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            await _output.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            // The recording stops; the media it watched carries on.
            LogWriteFailed(exception);
            while (_pending.Reader.TryRead(out (byte[] Buffer, int) left))
            {
                ArrayPool<byte>.Shared.Return(left.Buffer);
            }
        }
    }

    [LoggerMessage(
        2960,
        LogLevel.Warning,
        "The video recording fell behind the stream; frames are left out of it while it catches up."
    )]
    private partial void LogBehind();

    [LoggerMessage(
        2961,
        LogLevel.Error,
        "The video recording could not be written; it stops here."
    )]
    private partial void LogWriteFailed(Exception exception);
}
