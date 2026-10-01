using Agash.StreamTransport.Media;

namespace Agash.StreamTransport;

/// <summary>Connects a video source straight to a sink, converting on the way when the sink needs it.</summary>
public static class VideoRouteExtensions
{
    /// <summary>
    /// Routes <paramref name="source"/> into <paramref name="sink"/> with no session between them: a camera
    /// to a Spout or Syphon preview, a test signal into a v4l2loopback webcam. Frames the sink takes go
    /// through untouched; others pass a processor (or a chain of two) the registry picks for the frames'
    /// storage, format and GPU, remade when they change.
    /// </summary>
    /// <param name="registry">Where processors come from.</param>
    /// <param name="source">The video.</param>
    /// <param name="sink">Where it goes.</param>
    /// <returns>A handle that ends the route when disposed.</returns>
    public static IDisposable Route(
        this MediaCodecRegistry registry,
        IVideoSource source,
        IVideoSink sink
    )
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        VideoRoute route = new(registry, sink);
        return new Connection(route, source.Connect(route, sink.Constraints));
    }

    // Converts what the sink cannot take, keeping one processor for each kind of frame in turn.
    private sealed class VideoRoute(MediaCodecRegistry registry, IVideoSink sink)
        : IVideoFrameConsumer,
            IDisposable
    {
        private readonly Lock _gate = new();
        private VideoStreamDescription? _described;
        private IVideoProcessor? _processor;
        private bool _disposed;

        public void OnFrame(in VideoFrame frame)
        {
            if (sink.Constraints.Accepts(frame.Storage, frame.Format.PixelFormat))
            {
                sink.OnFrame(in frame);
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                VideoStreamDescription description = new(
                    frame.Storage.Kind,
                    frame.Format.PixelFormat,
                    new VideoSize(frame.Format.VisibleRect.Width, frame.Format.VisibleRect.Height),
                    frame.Storage.Device
                );
                if (description != _described)
                {
                    _processor?.Dispose();
                    _processor = registry.TryCreateVideoProcessor(
                        description,
                        new VideoProcessing(sink.Constraints),
                        out IVideoProcessor? processor
                    )
                        ? processor
                        : throw new InvalidOperationException(
                            $"No registered processor turns {description} into what the sink takes."
                        );
                    _described = description;
                }

                _processor!.Process(in frame, sink);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _processor?.Dispose();
                _processor = null;
            }
        }
    }

    private sealed class Connection(VideoRoute route, IDisposable connection) : IDisposable
    {
        public void Dispose()
        {
            connection.Dispose();
            route.Dispose();
        }
    }
}
