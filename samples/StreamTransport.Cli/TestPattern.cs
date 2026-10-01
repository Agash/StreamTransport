using System.Collections.Immutable;
using Agash.StreamTransport.Media;

namespace StreamTransport.Cli;

/// <summary>
/// A moving test pattern in memory, BGRA with an alpha ramp, pushed at a steady rate: something to
/// publish on a machine with nothing to share.
/// </summary>
internal sealed class TestPattern(VideoSize size, double frameRate) : IVideoSource, IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private ImmutableArray<IVideoFrameConsumer> _consumers = [];
    private Task? _loop;

    // Sessions connect from their own threads; one loop serves them all.
    public IDisposable Connect(IVideoFrameConsumer consumer, VideoConstraints constraints)
    {
        lock (_gate)
        {
            _consumers = _consumers.Add(consumer);
            _loop ??= RunAsync(_stop.Token);
        }

        return new Connection(this, consumer);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        byte[] pixels = new byte[size.Width * size.Height * 4];
        using PeriodicTimer frames = new(TimeSpan.FromSeconds(1 / frameRate));
        try
        {
            for (int frame = 0; await frames.WaitForNextTickAsync(cancellationToken); frame++)
            {
                Draw(pixels, frame);
                VideoFrame video = new(
                    new VideoFormat(PixelFormat.Bgra, size.Width, size.Height),
                    MediaTimestamp.Captured(MediaClock.System.Now),
                    pixels,
                    size.Width * 4,
                    color: VideoColor.Srgb
                );
                foreach (IVideoFrameConsumer consumer in _consumers)
                {
                    consumer.OnFrame(in video);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Deliberately not logged: cancellation is how the pattern stops.
        }
    }

    // Diagonal colour bands sliding right, over an alpha ramp from left to right.
    private void Draw(byte[] pixels, int frame)
    {
        for (int y = 0; y < size.Height; y++)
        {
            for (int x = 0; x < size.Width; x++)
            {
                int i = ((y * size.Width) + x) * 4;
                int band = (x + y + (frame * 4)) & 0xFF;
                pixels[i] = (byte)band;
                pixels[i + 1] = (byte)(255 - band);
                pixels[i + 2] = (byte)((band * 2) & 0xFF);
                pixels[i + 3] = (byte)(x * 255 / size.Width);
            }
        }
    }

    private sealed class Connection(TestPattern pattern, IVideoFrameConsumer consumer) : IDisposable
    {
        public void Dispose()
        {
            lock (pattern._gate)
            {
                pattern._consumers = pattern._consumers.Remove(consumer);
            }
        }
    }
}
