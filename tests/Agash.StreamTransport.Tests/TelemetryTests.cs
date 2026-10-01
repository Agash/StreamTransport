using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.Signaling;
using Microsoft.Extensions.DependencyInjection;

namespace Agash.StreamTransport.Tests;

[TestClass]
public sealed class TelemetryTests
{
    [TestMethod]
    [Timeout(60_000)]
    public async Task Session_ReportsItsMediaAndConnectToTheHostsMeterAndTracing()
    {
        using MeterRecorder meters = new();
        ConcurrentQueue<Activity> activities = new();
        using ActivityListener tracing = new()
        {
            ShouldListenTo = static source =>
                source.Name == StreamTransportDiagnostics.ActivitySourceName,
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue,
        };
        ActivitySource.AddActivityListener(tracing);

        await using (ServiceProvider services = MediaServices.Create(meters: meters))
        {
            IMediaSessionFactory factory = services.GetRequiredService<IMediaSessionFactory>();
            MediaSessionOptions options = MediaServices.Loopback(
                new MediaSessionOptions { VideoCodecs = [VideoCodecId.H264] }
            );
            RecordingVideoSink video = new(target: 10);
            RecordingAudioSink audio = new(target: 20);
            (LoopbackSignaling offer, LoopbackSignaling answer) = LoopbackSignaling.Pair();
            await using (offer)
            await using (answer)
            {
                await using IMediaSession sender = factory.Create(
                    offer,
                    MediaSessionRole.Offerer,
                    new MediaEndpoints
                    {
                        VideoSource = new GradientSource(TimeSpan.FromMilliseconds(33)),
                        AudioSource = new ToneSource(),
                    },
                    options
                );
                await using IMediaSession receiver = factory.Create(
                    answer,
                    MediaSessionRole.Answerer,
                    new MediaEndpoints { VideoSink = video, AudioSink = audio },
                    options
                );
                Assert.AreEqual(2, meters.Sum("streamtransport.sessions.active"));

                await receiver.StartAsync();
                await sender.StartAsync();
                await Task.WhenAll(sender.Connected, receiver.Connected)
                    .WaitAsync(TimeSpan.FromSeconds(20));
                await Task.WhenAll(video.Reached, audio.Reached)
                    .WaitAsync(TimeSpan.FromSeconds(20));
            }
        }

        Assert.AreEqual(
            0,
            meters.Sum("streamtransport.sessions.active"),
            "disposed sessions leave"
        );
        Assert.AreEqual(2, meters.Count("streamtransport.session.connect.duration", "connected"));
        Assert.IsGreaterThan(0, meters.Sum("streamtransport.video.frames.sent", "H264"));
        Assert.IsGreaterThan(0, meters.Sum("streamtransport.video.frames.decoded", "H264"));
        Assert.IsGreaterThan(0, meters.Count("streamtransport.video.encode.duration", "H264"));
        Assert.IsGreaterThan(0, meters.Count("streamtransport.video.decode.duration", "H264"));
        Assert.IsGreaterThan(0, meters.Count("streamtransport.video.target_bitrate", "H264"));
        Assert.IsGreaterThan(0, meters.Sum("streamtransport.audio.frames.sent", "opus"));
        Assert.IsGreaterThan(0, meters.Sum("streamtransport.audio.frames.decoded", "opus"));

        // The FFmpeg codecs report on their own meter, from the same container.
        Assert.IsGreaterThan(0, meters.Sum("streamtransport.ffmpeg.encoder.opens", "start"));
        Assert.IsGreaterThan(0, meters.Count("streamtransport.ffmpeg.encoder.open.duration"));

        Activity[] connects =
        [
            .. activities.Where(static a => a.OperationName == "streamtransport.session.connect"),
        ];
        Assert.HasCount(2, connects);
        Assert.IsTrue(
            connects.All(static a =>
                (string?)a.GetTagItem("streamtransport.outcome") == "connected"
            )
        );
    }

    // An IMeterFactory whose meters are this recorder's alone, as a host's container makes them, with a
    // listener that hears only those.
    private sealed class MeterRecorder : IMeterFactory
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<(
            string Instrument,
            double Value,
            string? Tag
        )> _measurements = new();

        public MeterRecorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Scope == this)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
            _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
            _listener.Start();
        }

        public Meter Create(MeterOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.Scope = this;
            return new Meter(options);
        }

        // The sum of an instrument's measurements, those tagged with a value when one is given.
        public double Sum(string instrument, string? tag = null) =>
            Matching(instrument, tag).Sum(static m => m.Value);

        public int Count(string instrument, string? tag = null) =>
            Matching(instrument, tag).Count();

        public void Dispose() => _listener.Dispose();

        private IEnumerable<(string Instrument, double Value, string? Tag)> Matching(
            string instrument,
            string? tag
        ) => _measurements.Where(m => m.Instrument == instrument && (tag is null || m.Tag == tag));

        private void Record(
            Instrument instrument,
            double value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags
        )
        {
            string? tag = null;
            foreach (KeyValuePair<string, object?> pair in tags)
            {
                if (
                    pair.Key
                    is "streamtransport.codec"
                        or "streamtransport.outcome"
                        or "streamtransport.reason"
                )
                {
                    tag = pair.Value as string;
                }
            }

            _measurements.Enqueue((instrument.Name, value, tag));
        }
    }
}
