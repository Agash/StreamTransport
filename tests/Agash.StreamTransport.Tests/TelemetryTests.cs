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

        // The WebRTC transport reports on its own meter, from the same container: both connections came
        // up directly between host candidates and went away with their sessions.
        Assert.AreEqual(2, meters.Count("streamtransport.webrtc.connect.duration", "connected"));
        Assert.AreEqual(0, meters.Sum("streamtransport.webrtc.connections.active"));
        Assert.IsGreaterThanOrEqualTo(
            2,
            meters.Sum("streamtransport.webrtc.ice.selected_paths", "host")
        );
        Assert.IsGreaterThan(0, meters.Sum("streamtransport.webrtc.rtp.packets.sent"));

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
}
