using Agash.StreamTransport.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Agash.StreamTransport.Tests;

// Two sessions in one process over loopback ICE, DTLS and SRTP, with the real codecs.
[TestClass]
[TestCategory("Integration")]
public sealed class MediaSessionTests
{
    public static IEnumerable<object[]> Codecs =>
        [
            ["H264"],
            ["H265"],
            ["AV1"],
        ];

    [TestMethod]
    [Timeout(60_000)]
    [DynamicData(nameof(Codecs))]
    public async Task Session_VideoAndAudio_ArriveDecoded(string codec)
    {
        CapturingLoggerFactory logs = new();
        await using ServiceProvider services = MediaServices.Create(logs);
        IMediaSessionFactory factory = services.GetRequiredService<IMediaSessionFactory>();
        MediaSessionOptions options = MediaServices.Loopback(
            new MediaSessionOptions { VideoCodecs = [new VideoCodecId(codec)] }
        );
        RecordingVideoSink video = new(target: 20);
        RecordingAudioSink audio = new(target: 50);
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

            await receiver.StartAsync();
            await sender.StartAsync();
            await Task.WhenAll(sender.Connected, receiver.Connected)
                .WaitAsync(TimeSpan.FromSeconds(20));
            await Task.WhenAll(video.Reached, audio.Reached).WaitAsync(TimeSpan.FromSeconds(20));

            Assert.AreEqual(GradientSource.Width, video.LastFormat.VisibleRect.Width);
            foreach (double luma in video.Lumas)
            {
                Assert.AreEqual(GradientSource.MeanLuma(0), luma, 8.0, logs.Dump());
            }

            Assert.AreEqual(0.354, audio.Rms, 0.1, "a half-scale sine keeps its level");
            Assert.AreEqual(0, receiver.Statistics.VideoFramesFailed);
            Assert.IsGreaterThan(0, sender.Statistics.VideoFramesSent);
            Assert.IsGreaterThan(0, sender.Statistics.AudioFramesSent);
            Assert.IsGreaterThan(0, receiver.Statistics.AudioFramesDecoded);
            Assert.IsNotNull(sender.Route, "a connected session knows its path");
            Assert.AreEqual(sender.Route.Value.Local, receiver.Route!.Value.Remote);
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Session_AudioSourceThatCannotStart_StillSendsVideo()
    {
        await using ServiceProvider services = MediaServices.Create();
        IMediaSessionFactory factory = services.GetRequiredService<IMediaSessionFactory>();
        MediaSessionOptions options = MediaServices.Loopback(new MediaSessionOptions());
        RecordingVideoSink video = new(target: 10);
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
                    AudioSource = new MissingAudioDevice(),
                },
                options
            );
            await using IMediaSession receiver = factory.Create(
                answer,
                MediaSessionRole.Answerer,
                new MediaEndpoints { VideoSink = video, AudioSink = new RecordingAudioSink(1) },
                options
            );

            await receiver.StartAsync();
            await sender.StartAsync();
            await Task.WhenAll(sender.Connected, receiver.Connected)
                .WaitAsync(TimeSpan.FromSeconds(20));
            await video.Reached.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.AreEqual(0, sender.Statistics.AudioFramesSent);
            Assert.IsGreaterThan(0, sender.Statistics.VideoFramesSent);
        }
    }

    // A source on a machine with no microphone: it refuses every consumer.
    private sealed class MissingAudioDevice : IAudioSource
    {
        public AudioFormat Format { get; } = new(SampleFormat.F32, 48_000, 2);

        public IDisposable Connect(IAudioFrameConsumer consumer) =>
            throw new InvalidOperationException("The system has no audio input device.");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Session_SyncedPlayout_HoldsBothStreamsInItsBuffer()
    {
        await using ServiceProvider services = MediaServices.Create();
        IMediaSessionFactory factory = services.GetRequiredService<IMediaSessionFactory>();
        MediaSessionOptions options = MediaServices.Loopback(
            MediaSessionOptions.For(MediaProfile.IrlContribution) with
            {
                VideoCodecs = [VideoCodecId.H264],
            }
        );
        RecordingVideoSink video = new(target: 20);
        RecordingAudioSink audio = new(target: 50);
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

            await receiver.StartAsync();
            await sender.StartAsync();
            await Task.WhenAll(video.Reached, audio.Reached).WaitAsync(TimeSpan.FromSeconds(30));

            TimeSpan delay = receiver.Statistics.PlayoutDelay;
            Assert.IsTrue(
                delay >= options.MinPlayoutDelay && delay <= options.MaxPlayoutDelay,
                $"The playout buffer is {delay}."
            );
        }
    }

    [TestMethod]
    public void Create_WithoutSourcesOrSinks_Throws()
    {
        using ServiceProvider services = MediaServices.Create();
        IMediaSessionFactory factory = services.GetRequiredService<IMediaSessionFactory>();
        (LoopbackSignaling offer, _) = LoopbackSignaling.Pair();

        Assert.ThrowsExactly<ArgumentException>(() =>
            factory.Create(
                offer,
                MediaSessionRole.Offerer,
                new MediaEndpoints(),
                new MediaSessionOptions()
            )
        );
    }
}
