using System.Runtime.InteropServices;
using Agash.StreamTransport.MacOS.Audio;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.MacOS.Tests;

[TestClass]
public sealed class CoreAudioTests
{
    // Plays a quiet tone on the default output for a moment.
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(20_000)]
    public async Task Sink_PlaysOnTheDefaultOutput()
    {
        CoreAudioSink sink;
        try
        {
            sink = new CoreAudioSink();
        }
        catch (InvalidOperationException exception)
        {
            Assert.Inconclusive($"No audio output: {exception.Message}");
            return;
        }

        using (sink)
        {
            Assert.IsTrue(
                sink.OutputLatency >= TimeSpan.Zero && sink.OutputLatency < TimeSpan.FromSeconds(1),
                $"Latency {sink.OutputLatency}."
            );
            using PeriodicTimer period = new(TimeSpan.FromMilliseconds(10));
            for (int chunk = 0; chunk < 30 && await period.WaitForNextTickAsync(); chunk++)
            {
                float[] samples = new float[480 * 2];
                for (int s = 0; s < 480; s++)
                {
                    float value =
                        0.02f * MathF.Sin(2 * MathF.PI * 1000 * ((chunk * 480) + s) / 48_000f);
                    samples[s * 2] = value;
                    samples[(s * 2) + 1] = value;
                }

                sink.OnFrame(
                    new AudioFrame(
                        MemoryMarshal.AsBytes(samples.AsSpan()),
                        CoreAudioSink.Format,
                        MediaTimestamp.Captured(MediaClock.System.Now)
                    )
                );
            }
        }
    }

    [TestMethod]
    public void Sink_RefusesOtherFormats()
    {
        CoreAudioSink sink;
        try
        {
            sink = new CoreAudioSink();
        }
        catch (InvalidOperationException exception)
        {
            Assert.Inconclusive($"No audio output: {exception.Message}");
            return;
        }

        using (sink)
        {
            Assert.ThrowsExactly<ArgumentException>(() =>
                sink.OnFrame(
                    new AudioFrame(
                        new byte[480 * 2],
                        new AudioFormat(SampleFormat.S16, 48_000, 1),
                        MediaTimestamp.Captured(new MediaTime(1))
                    )
                )
            );
        }
    }

    // Hears the default input; without a microphone, or before the user allows one, there is nothing
    // to hear.
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(20_000)]
    public async Task Source_DeliversStereoFloatWithRisingCaptureTimes()
    {
        using CoreAudioSource source = new();
        Heard heard = new(frames: 10);
        IDisposable connection;
        try
        {
            connection = source.Connect(heard);
        }
        catch (InvalidOperationException exception)
        {
            Assert.Inconclusive($"No audio input: {exception.Message}");
            return;
        }

        using (connection)
        {
            await heard.Done.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.AreEqual(CoreAudioSink.Format, heard.Format);
        Assert.AreEqual(TimestampKind.Capture, heard.Kind);
        Assert.IsTrue(heard.Rising, "capture times rise");
        TimeSpan offset = MediaClock.System.Now - heard.Last;
        Assert.IsLessThan(
            TimeSpan.FromSeconds(1),
            offset.Duration(),
            $"The last capture is {offset} from now."
        );
    }

    private sealed class Heard(int frames) : IAudioFrameConsumer
    {
        private readonly TaskCompletionSource _done = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _count;

        public Task Done => _done.Task;

        public AudioFormat Format { get; private set; }

        public TimestampKind Kind { get; private set; }

        public bool Rising { get; private set; } = true;

        public MediaTime Last { get; private set; }

        public void OnFrame(in AudioFrame frame)
        {
            Format = frame.Format;
            Kind = frame.Timestamp.Kind;
            Rising &= frame.Timestamp.Time > Last;
            Last = frame.Timestamp.Time;
            if (++_count >= frames)
            {
                _done.TrySetResult();
            }
        }
    }
}
