using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Windows.Tests;

// Test textures outlive the processing, so retaining them needs no reference.
internal sealed class NotRetained : IVideoFrameRetainer
{
    public VideoFrameLease Retain(in VideoFrame frame) => new Borrowed(in frame);

    private sealed class Borrowed(in VideoFrame frame)
        : VideoFrameLease(
            frame.Storage,
            frame.Format,
            frame.Timestamp,
            frame.Color,
            frame.Orientation,
            frame.Duration
        )
    {
        protected override void Release() { }
    }
}
