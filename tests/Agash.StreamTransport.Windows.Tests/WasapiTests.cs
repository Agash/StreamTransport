using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.Windows.Wasapi;

namespace Agash.StreamTransport.Windows.Tests;

[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class WasapiTests
{
    [TestMethod]
    public void Ring_ReadsInOrderAndDropsTheOldestWhenFull()
    {
        FloatRing ring = new(4);
        ring.Write([1, 2, 3]);
        ring.Write([4, 5]);

        float[] read = new float[6];
        Assert.AreEqual(4, ring.Read(read));
        CollectionAssert.AreEqual(new float[] { 2, 3, 4, 5, 0, 0 }, read);
        Assert.AreEqual(0, ring.Read(read));

        ring.Write([6, 7, 8, 9, 10]);
        Assert.AreEqual(4, ring.Read(read));
        CollectionAssert.AreEqual(new float[] { 7, 8, 9, 10 }, read[..4]);
    }

    // Plays a quiet tone on the default output for a moment and hears it back through loopback.
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(20_000)]
    public async Task SinkToLoopbackSource_CarriesTheToneWithCaptureTimes()
    {
        using WasapiAudioSink sink = new();
        TimeSpan latency;
        try
        {
            latency = await sink.OutputLatency.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (COMException exception)
        {
            Assert.Inconclusive($"No audio output: {exception.Message}");
            return;
        }

        Assert.IsTrue(
            latency > TimeSpan.Zero && latency < TimeSpan.FromSeconds(1),
            $"Latency {latency}."
        );
        using WasapiAudioSource source = new(WasapiEndpoint.DefaultOutputLoopback);
        Heard heard = new(samples: 48_000 / 4);
        using IDisposable connection = source.Connect(heard);

        // Half a second of tone, pushed a period at a time as a decoder would.
        using (PeriodicTimer period = new(TimeSpan.FromMilliseconds(10)))
        {
            for (int chunk = 0; chunk < 50 && await period.WaitForNextTickAsync(); chunk++)
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
                        WasapiClient.Format,
                        MediaTimestamp.Captured(MediaClock.System.Now)
                    )
                );
            }
        }

        await heard.Done.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsGreaterThan(0.005, heard.Rms, "the tone came back");
        Assert.AreEqual(TimestampKind.Capture, heard.Kind);
        Assert.IsTrue(heard.Monotonic, "capture times rise");
        // Loopback audio is stamped with when it plays at the device, which can be ahead of now by the
        // output buffer; either way it is on the media clock, within a second of it.
        TimeSpan offset = MediaClock.System.Now - heard.Last;
        Assert.IsLessThan(
            TimeSpan.FromSeconds(1),
            offset.Duration(),
            $"The last capture is {offset} from now."
        );
    }

    // Measures the loudest quarter second it hears after the tone starts.
    private sealed class Heard(int samples) : IAudioFrameConsumer
    {
        private readonly TaskCompletionSource _done = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private double _sum;
        private int _count;

        public Task Done => _done.Task;

        public double Rms { get; private set; }

        public TimestampKind Kind { get; private set; }

        public bool Monotonic { get; private set; } = true;

        public MediaTime Last { get; private set; }

        public void OnFrame(in AudioFrame frame)
        {
            Kind = frame.Timestamp.Kind;
            Monotonic &= frame.Timestamp.Time > Last;
            Last = frame.Timestamp.Time;
            foreach (float value in MemoryMarshal.Cast<byte, float>(frame.Samples))
            {
                if (value != 0 || _count > 0)
                {
                    _sum += value * value;
                    _count++;
                }
            }

            if (_count >= samples * 2)
            {
                Rms = Math.Sqrt(_sum / _count);
                _done.TrySetResult();
            }
        }
    }
}
