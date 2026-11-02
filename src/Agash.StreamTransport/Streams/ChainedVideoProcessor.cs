using Agash.StreamTransport.Media;

namespace Agash.StreamTransport;

/// <summary>Two processors one after the other: each frame the first makes goes straight into the second.</summary>
internal sealed class ChainedVideoProcessor(IVideoProcessor first, IVideoProcessor second)
    : IVideoProcessor
{
    private readonly Handoff _handoff = new(second);

    /// <inheritdoc/>
    public VideoProcessorInfo Info { get; } =
        second.Info with
        {
            ImplementationName =
                $"{first.Info.ImplementationName} then {second.Info.ImplementationName}",
        };

    /// <inheritdoc/>
    public void Process(in VideoFrame frame, IVideoFrameConsumer consumer)
    {
        _handoff.Consumer = consumer;
        first.Process(in frame, _handoff);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        second.Dispose();
        first.Dispose();
    }

    // The first processor's output, handed to the second within the same call.
    private sealed class Handoff(IVideoProcessor second) : IVideoFrameConsumer
    {
        public IVideoFrameConsumer? Consumer { get; set; }

        public void OnFrame(in VideoFrame frame) => second.Process(in frame, Consumer!);
    }
}
