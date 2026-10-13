using Agash.StreamTransport.Media;
using Agash.StreamTransport.WebRtc.Transport;
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

    // A 44.1 kHz mono source into 48 kHz Opus, played to a sink that takes only 44.1 kHz stereo: both
    // ends convert, and the tone keeps its level and its pitch.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Session_AudioAtRatesTheCodecDoesNotTake_IsConvertedBothWays()
    {
        await using ServiceProvider services = MediaServices.Create();
        IMediaSessionFactory factory = services.GetRequiredService<IMediaSessionFactory>();
        MediaSessionOptions options = MediaServices.Loopback(new MediaSessionOptions());
        RecordingAudioSink audio = new(target: 100, rate: 44_100, channels: 2);
        (LoopbackSignaling offer, LoopbackSignaling answer) = LoopbackSignaling.Pair();
        await using (offer)
        await using (answer)
        {
            await using IMediaSession sender = factory.Create(
                offer,
                MediaSessionRole.Offerer,
                new MediaEndpoints { AudioSource = new ToneSource(rate: 44_100, channels: 1) },
                options
            );
            await using IMediaSession receiver = factory.Create(
                answer,
                MediaSessionRole.Answerer,
                new MediaEndpoints { AudioSink = audio },
                options
            );

            await receiver.StartAsync();
            await sender.StartAsync();
            await Task.WhenAll(sender.Connected, receiver.Connected)
                .WaitAsync(TimeSpan.FromSeconds(20));
            await audio.Reached.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.AreEqual(new AudioFormat(SampleFormat.F32, 44_100, 2), audio.LastFormat);
            Assert.AreEqual(0.354, audio.Rms, 0.1, "a half-scale sine keeps its level");
            Assert.AreEqual(
                1000,
                audio.Frequency,
                25,
                "the tone keeps its pitch through both rates"
            );
        }
    }

    // A source whose times were only observed sends no capture time (abs-capture-time cannot say so); the
    // receiver aligns it from the sender's reports and plays it all the same.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Session_ObservedAudio_PlaysWithoutACaptureTime()
    {
        await using ServiceProvider services = MediaServices.Create();
        IMediaSessionFactory factory = services.GetRequiredService<IMediaSessionFactory>();
        MediaSessionOptions options = MediaServices.Loopback(new MediaSessionOptions());
        RecordingAudioSink audio = new(target: 100);
        (LoopbackSignaling offer, LoopbackSignaling answer) = LoopbackSignaling.Pair();
        await using (offer)
        await using (answer)
        {
            await using IMediaSession sender = factory.Create(
                offer,
                MediaSessionRole.Offerer,
                new MediaEndpoints { AudioSource = new ToneSource(observed: true) },
                options
            );
            await using IMediaSession receiver = factory.Create(
                answer,
                MediaSessionRole.Answerer,
                new MediaEndpoints { AudioSink = audio },
                options
            );

            await receiver.StartAsync();
            await sender.StartAsync();
            await Task.WhenAll(sender.Connected, receiver.Connected)
                .WaitAsync(TimeSpan.FromSeconds(20));
            await audio.Reached.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.AreEqual(0.354, audio.Rms, 0.1, "a half-scale sine keeps its level");
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

    // A sender asking for frames to play on arrival (the playout-delay extension at zero) takes the
    // receiver's buffer below its own 40 ms floor.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Session_SenderAskingForNoPlayoutDelay_EmptiesTheReceiversBuffer()
    {
        await using ServiceProvider services = MediaServices.Create();
        IMediaSessionFactory factory = services.GetRequiredService<IMediaSessionFactory>();
        MediaSessionOptions receiving = MediaServices.Loopback(
            new MediaSessionOptions { VideoCodecs = [VideoCodecId.H264] }
        );
        MediaSessionOptions sending = receiving with
        {
            Transport = new WebRtcTransportOptions
            {
                IncludeLoopbackCandidates = true,
                RequestedPlayoutDelay = new PlayoutDelayRange(TimeSpan.Zero, TimeSpan.Zero),
            },
        };
        RecordingVideoSink video = new(target: 30);
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
                },
                sending
            );
            await using IMediaSession receiver = factory.Create(
                answer,
                MediaSessionRole.Answerer,
                new MediaEndpoints { VideoSink = video },
                receiving
            );

            await receiver.StartAsync();
            await sender.StartAsync();
            await video.Reached.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.IsLessThan(
                receiving.MinPlayoutDelay,
                receiver.Statistics.PlayoutDelay,
                "the sender's request overrides the receiver's floor"
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
