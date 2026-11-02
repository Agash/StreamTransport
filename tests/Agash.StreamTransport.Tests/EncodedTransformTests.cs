using Agash.StreamTransport.Media;
using Agash.StreamTransport.Sessions;
using Agash.StreamTransport.TestSignal;
using Microsoft.Extensions.DependencyInjection;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// Encoded transforms on a session, after the W3C WebRTC Encoded Transform: the sender's sees every frame
/// the encoder makes before it is packetized, the receiver's every frame before it is decoded, and what
/// a transform does not pass on goes no further.
/// </summary>
[TestClass]
public sealed class EncodedTransformTests
{
    [TestMethod]
    [Timeout(60_000)]
    public async Task Transforms_SeeEveryFrame_OnBothSides()
    {
        Counting send = new();
        Counting receive = new();
        CountingSink sink = new(target: 20);

        await RunAsync(send, receive, sink, sink.Reached);

        Assert.IsGreaterThanOrEqualTo(20, receive.Frames);
        Assert.IsGreaterThanOrEqualTo(
            receive.Frames,
            send.Frames,
            "the receiver sees no more than was sent"
        );
        Assert.IsTrue(receive.Keyframes > 0, "the first frame received is a keyframe");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ReceiveTransform_ThatPassesNothingOn_LeavesTheDecoderIdle()
    {
        Counting receive = new(passOn: false);
        CountingSink sink = new(target: 1);

        await RunAsync(new Counting(), receive, sink, receive.Seen(20));

        Assert.AreEqual(0, sink.Frames);
    }

    [TestMethod]
    public async Task Recorder_PassesFramesOn_AndWritesTheirBytesInOrder()
    {
        using MemoryStream output = new();
        EncodedVideoRecorder recorder = new(output);
        Counting next = new();
        byte[][] units =
        [
            [0, 0, 0, 1, 0x40, 1],
            [0, 0, 1, 0x26, 2, 3],
            [0, 0, 1, 0x02, 4],
        ];
        for (int i = 0; i < units.Length; i++)
        {
            recorder.Transform(
                new EncodedVideoFrame(
                    units[i],
                    VideoCodecId.H265,
                    i == 0,
                    MediaTimestamp.Captured(new MediaTime(i))
                ),
                next
            );
        }

        await recorder.DisposeAsync();

        Assert.AreEqual(3, next.Frames);
        Assert.AreEqual(3, recorder.FramesRecorded);
        Assert.AreEqual(0, recorder.FramesDropped);
        CollectionAssert.AreEqual(units.SelectMany(static u => u).ToArray(), output.ToArray());
    }

    private static async Task RunAsync(
        IEncodedVideoTransform send,
        IEncodedVideoTransform receive,
        IVideoSink sink,
        Task done
    )
    {
        await using ServiceProvider services = MediaServices.Create();
        IMediaSessionFactory factory = services.GetRequiredService<IMediaSessionFactory>();
        MediaSessionOptions options = MediaServices.Loopback(
            new MediaSessionOptions { VideoCodecs = [VideoCodecId.H264] }
        );
        TestSignalGenerator generator = new();
        using IVideoInput video = await new TestSignalVideoInputProvider(generator).OpenAsync(
            new VideoInputInfo("test", "signal", "Test signal", MediaInputKind.Generated, []),
            new VideoInputRequest { Size = new VideoSize(320, 180), FrameRate = 30 },
            CancellationToken.None
        );
        (LoopbackSignaling offer, LoopbackSignaling answer) = LoopbackSignaling.Pair();
        await using (offer)
        await using (answer)
        {
            await using IMediaSession sender = factory.Create(
                offer,
                MediaSessionRole.Offerer,
                new MediaEndpoints { VideoSource = video, VideoSendTransform = send },
                options
            );
            await using IMediaSession receiver = factory.Create(
                answer,
                MediaSessionRole.Answerer,
                new MediaEndpoints { VideoSink = sink, VideoReceiveTransform = receive },
                options
            );

            await receiver.StartAsync();
            await sender.StartAsync();
            await done.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    // Counts frames, keyframes among them, and passes them on unless told not to.
    private sealed class Counting(bool passOn = true)
        : IEncodedVideoTransform,
            IEncodedVideoConsumer
    {
        private readonly List<(int Count, TaskCompletionSource Reached)> _waits = [];
        private int _frames;

        public int Frames => Volatile.Read(ref _frames);

        public int Keyframes { get; private set; }

        public Task Seen(int count)
        {
            TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waits)
            {
                _waits.Add((count, reached));
            }

            return reached.Task;
        }

        public void Transform(in EncodedVideoFrame frame, IEncodedVideoConsumer next)
        {
            OnEncoded(in frame);
            if (passOn)
            {
                next.OnEncoded(in frame);
            }
        }

        public void OnEncoded(in EncodedVideoFrame frame)
        {
            int frames = Interlocked.Increment(ref _frames);
            if (frame.Keyframe)
            {
                Keyframes++;
            }

            lock (_waits)
            {
                foreach ((int count, TaskCompletionSource reached) in _waits)
                {
                    if (frames >= count)
                    {
                        _ = reached.TrySetResult();
                    }
                }
            }
        }
    }

    private sealed class CountingSink(int target) : IVideoSink
    {
        private readonly TaskCompletionSource _reached = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _frames;

        public int Frames => Volatile.Read(ref _frames);

        public Task Reached => _reached.Task;

        public VideoConstraints Constraints { get; } =
            VideoConstraints.Cpu(PixelFormat.Nv12, PixelFormat.I420);

        public void OnFrame(in VideoFrame frame)
        {
            if (Interlocked.Increment(ref _frames) >= target)
            {
                _ = _reached.TrySetResult();
            }
        }
    }
}
