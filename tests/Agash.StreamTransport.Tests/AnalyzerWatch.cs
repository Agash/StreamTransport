using Agash.StreamTransport.Media;
using Agash.StreamTransport.TestSignal;

namespace Agash.StreamTransport.Tests;

// Lets a test wait for what a TestSignalAnalyzer has measured without polling it: the outputs it hands
// out re-check the waiting conditions after each frame the analyzer has taken in.
internal sealed class AnalyzerWatch(TestSignalAnalyzer analyzer)
{
    private readonly Lock _gate = new();
    private readonly List<(
        Func<TestSignalMeasurement, bool> Condition,
        TaskCompletionSource Done
    )> _waiters = [];

    public TestSignalAnalyzer Analyzer { get; } = analyzer;

    public IVideoSink WrapVideo(IVideoSink? inner = null) =>
        new VideoTap(this, Analyzer.WrapVideo(inner));

    public IAudioSink WrapAudio(IAudioSink? inner = null) =>
        new AudioTap(this, Analyzer.WrapAudio(inner));

    // Completes when the measurement meets the condition, checked now and after each frame.
    public async Task UntilAsync(
        Func<TestSignalMeasurement, bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    )
    {
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _waiters.Add((condition, done));
        }

        Check();
        try
        {
            await done.Task.WaitAsync(timeout, cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                _ = _waiters.RemoveAll(w => w.Done == done);
            }
        }
    }

    private void Check()
    {
        (Func<TestSignalMeasurement, bool> Condition, TaskCompletionSource Done)[] waiting;
        lock (_gate)
        {
            if (_waiters.Count == 0)
            {
                return;
            }

            waiting = [.. _waiters];
        }

        TestSignalMeasurement measured = Analyzer.Measure();
        foreach (
            (Func<TestSignalMeasurement, bool> condition, TaskCompletionSource done) in waiting
        )
        {
            if (condition(measured))
            {
                _ = done.TrySetResult();
            }
        }
    }

    private sealed class VideoTap(AnalyzerWatch watch, IVideoSink inner) : IVideoSink
    {
        public VideoConstraints Constraints => inner.Constraints;

        public TimeSpan? OutputLatency => inner.OutputLatency;

        public void OnFrame(in VideoFrame frame)
        {
            inner.OnFrame(in frame);
            watch.Check();
        }
    }

    private sealed class AudioTap(AnalyzerWatch watch, IAudioSink inner) : IAudioSink
    {
        public AudioConstraints Constraints => inner.Constraints;

        public TimeSpan? OutputLatency => inner.OutputLatency;

        public void OnFrame(in AudioFrame frame)
        {
            inner.OnFrame(in frame);
            watch.Check();
        }
    }
}
