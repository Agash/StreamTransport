using System.Threading.Channels;
using Agash.StreamTransport.Codecs.FFmpeg;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.Windows.MediaFoundation;
using FFmpeg.Interop.Native;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Windows.Tests;

// Against the machine's first Media Foundation camera; inconclusive on a machine without one.
[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class MediaFoundationTests
{
    private const int EncodedFrames = 30;
    private const int ReferenceFrame = 10;

    private static readonly TimeSpan FrameWait = TimeSpan.FromSeconds(10);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Connect_InMemory_DeliversTheCameraMode()
    {
        using IVideoInput input = await OpenAsync();
        VideoInputMode mode = input.Mode!.Value;
        Frames frames = new();
        using (input.Connect(frames, VideoConstraints.Cpu(mode.PixelFormat)))
        {
            Seen seen = await frames.NextAsync(TestContext.CancellationToken);
            Assert.AreEqual(VideoStorageKind.Cpu, seen.Storage);
            Assert.AreEqual(mode.PixelFormat, seen.Format.PixelFormat);
            Assert.AreEqual(mode.Size, seen.Format.CodedSize);
        }
    }

    [TestMethod]
    public async Task Connect_HardwareEncoderConstraints_DeliversNv12TexturesThatCanBeKept()
    {
        using var gpu = TestGpu.Open();
        using IVideoInput input = await OpenAsync();
        VideoSize size = input.Mode!.Value.Size;
        Frames frames = new(keep: 4);
        using (input.Connect(frames, Encoder(gpu.Adapter)))
        {
            for (int i = 0; i < 6; i++)
            {
                Seen seen = await frames.NextAsync(TestContext.CancellationToken);
                Assert.AreEqual(VideoStorageKind.D3D11, seen.Storage);
                Assert.AreEqual(gpu.Adapter, seen.Device);
                Assert.AreEqual(PixelFormat.Nv12, seen.Format.PixelFormat);
                Assert.AreEqual(
                    new VideoRect(0, 0, size.Width, size.Height),
                    seen.Format.VisibleRect
                );
            }
        }

        // Kept frames hold their samples past the capture's end.
        Assert.AreEqual(4, frames.Kept);
        frames.ReleaseKept();
    }

    [TestMethod]
    public async Task Connect_ConsumerInMemoryJoins_ReadsInMemoryUntilItLeaves()
    {
        using var gpu = TestGpu.Open();
        using IVideoInput input = await OpenAsync();
        VideoInputMode mode = input.Mode!.Value;
        Frames encoder = new();
        using (input.Connect(encoder, Encoder(gpu.Adapter)))
        {
            await encoder.UntilAsync(VideoStorageKind.D3D11, TestContext.CancellationToken);
            Frames preview = new();
            using (input.Connect(preview, VideoConstraints.Cpu(mode.PixelFormat)))
            {
                // Only frames it takes reach the preview, from the first one on.
                Seen first = await preview.NextAsync(TestContext.CancellationToken);
                Assert.AreEqual(VideoStorageKind.Cpu, first.Storage);
                Assert.AreEqual(mode.PixelFormat, first.Format.PixelFormat);
                await encoder.UntilAsync(VideoStorageKind.Cpu, TestContext.CancellationToken);
            }

            await encoder.UntilAsync(VideoStorageKind.D3D11, TestContext.CancellationToken);
        }
    }

    [TestMethod]
    public async Task Connect_Nvenc_EncodesTheCameraPictureFromItsTextures()
    {
        FFmpegVideoEncoderFactory factory = new(EncoderBackend.Nvenc);
        VideoCodecFormat format = new(VideoCodecId.H264);
        if (factory.QueryCapabilities(format, device: null) is not { } encoder)
        {
            Assert.Inconclusive("NVENC cannot encode H.264 on this machine.");
            return;
        }

        if (!encoder.Input.Storages.Contains(VideoStorageKind.D3D11))
        {
            Assert.Inconclusive($"{encoder.ImplementationName} takes no Direct3D 11 textures.");
        }

        using IVideoInput input = await OpenAsync();
        VideoInputMode mode = input.Mode!.Value;
        using IVideoEncoder video = factory.Create(
            new VideoEncoderConfiguration(format, mode.Size, new RateTarget(8_000_000, 30)),
            device: null
        );
        Encoding encoding = new(video);
        using (input.Connect(encoding, encoder.Input))
        {
            await encoding.Enough.Task.WaitAsync(FrameWait, TestContext.CancellationToken);
        }

        video.Flush(encoding);
        Assert.AreEqual(EncodedFrames, encoding.Textures, "A frame reached the encoder in memory.");

        // The texture read back from the GPU against what came out of the encoder, for one frame.
        Assert.AreEqual(encoding.Reference, DecodedMeanLuma(encoding.Units)[ReferenceFrame], 4.0);
    }

    [TestMethod]
    public async Task Dispose_CameraSendsNothing_ReturnsPromptly()
    {
        // STREAMTRANSPORT_TEST_SILENT_CAMERA names a camera that delivers no frames, such as an NDI
        // Webcam with no source selected.
        string? named = Environment.GetEnvironmentVariable("STREAMTRANSPORT_TEST_SILENT_CAMERA");
        if (named is null)
        {
            Assert.Inconclusive("STREAMTRANSPORT_TEST_SILENT_CAMERA is not set.");
        }

        using IVideoInput input = await OpenAsync(named);
        Frames frames = new();
        IDisposable connection = input.Connect(
            frames,
            VideoConstraints.Cpu(input.Mode!.Value.PixelFormat)
        );
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.CancellationToken);
        await Task.Run(connection.Dispose, TestContext.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
    }

    private static List<double> DecodedMeanLuma(List<byte[]> units)
    {
        using var decoder = FF.Decoder.Create(FF.Codec.FindDecoder("h264"));
        using FF.Packet packet = new();
        using FF.Frame output = new();
        List<double> means = [];
        foreach (byte[] unit in units)
        {
            packet.CopyFrom(unit);
            foreach (FF.Frame frame in decoder.Decode(packet, output))
            {
                means.Add(MeanOf(frame.GetPlane(0)));
            }
        }

        foreach (FF.Frame frame in decoder.Decode(null, output))
        {
            means.Add(MeanOf(frame.GetPlane(0)));
        }

        return means;
    }

    private static double MeanOf(FF.ReadOnlyImagePlane plane)
    {
        long sum = 0;
        for (int row = 0; row < plane.Height; row++)
        {
            foreach (byte value in plane.GetRow(row))
            {
                sum += value;
            }
        }

        return sum / (double)(plane.Height * plane.RowLength);
    }

    // What a hardware encoder on the adapter asks of its source.
    private static VideoConstraints Encoder(GpuIdentity adapter) =>
        new([VideoStorageKind.Cpu, VideoStorageKind.D3D11], [PixelFormat.Nv12], adapter);

    private async Task<IVideoInput> OpenAsync(string? name = null)
    {
        MediaFoundationVideoInputProvider provider = new(
            new ConsoleLogs(Microsoft.Extensions.Logging.LogLevel.Information)
        );
        var inputs = await provider.GetInputsAsync(TestContext.CancellationToken);
        if (inputs.IsEmpty)
        {
            Assert.Inconclusive("This machine has no Media Foundation camera.");
        }

        // STREAMTRANSPORT_TEST_CAMERA names the camera to use, by the start of its name; a machine's
        // first camera may be a virtual one nothing drives.
        string? named = name ?? Environment.GetEnvironmentVariable("STREAMTRANSPORT_TEST_CAMERA");
        VideoInputInfo? camera = named is null
            ? inputs[0]
            : inputs.FirstOrDefault(i =>
                i.Name.StartsWith(named, StringComparison.OrdinalIgnoreCase)
            );
        if (camera is null)
        {
            Assert.Inconclusive($"No camera is named {named}.");
        }

        TestContext.WriteLine($"{camera.Name}: {camera.Modes[0]}");
        return await provider.OpenAsync(
            camera,
            new VideoInputRequest
            {
                Size = camera.Modes[0].Size,
                FrameRate = camera.Modes[0].FrameRate,
                PixelFormat = camera.Modes[0].PixelFormat,
            },
            TestContext.CancellationToken
        );
    }

    // Hands frames to an encoder as a send stream does, from the reader's thread.
    private sealed class Encoding(IVideoEncoder encoder)
        : IVideoFrameConsumer,
            IEncodedVideoConsumer
    {
        private int _frames;

        public TaskCompletionSource Enough { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<byte[]> Units { get; } = [];

        public int Textures { get; private set; }

        public double Reference { get; private set; }

        public void OnFrame(in VideoFrame frame)
        {
            if (_frames == EncodedFrames)
            {
                return;
            }

            if (frame.Storage.TryGetValue(out D3D11Image image))
            {
                Textures++;
                if (_frames == ReferenceFrame)
                {
                    Reference = ReadBack(image, frame.Format);
                }
            }

            encoder.Encode(in frame, new EncodeRequest(Keyframe: _frames == 0), this);
            if (++_frames == EncodedFrames)
            {
                Enough.TrySetResult();
            }
        }

        public void OnEncoded(in EncodedVideoFrame frame) => Units.Add(frame.Data.ToArray());

        // The picture's mean luma, copied out of the texture on the GPU.
        private static unsafe double ReadBack(D3D11Image image, VideoFormat format)
        {
            using var device = FF.D3D11VAExtensions.FromD3D11Texture(image.Texture);
            using var pool = FF.HardwareFramePool.Create(
                device,
                FF.PixelFormat.D3D11,
                FF.PixelFormat.Nv12,
                format.CodedSize.Width,
                format.CodedSize.Height,
                initialSize: 1,
                configure: pool => ((AVD3D11VAFramesContext*)pool.Context->hwctx)->BindFlags = 0x200
            );
            using FF.Frame copy = new();
            using FF.Frame memory = new();
            FF.D3D11VAExtensions.CopyFromD3D11Texture(pool, image.Texture, image.Subresource, copy);
            copy.TransferTo(memory);
            return MeanOf(memory.GetPlane(0));
        }
    }

    private readonly record struct Seen(
        VideoStorageKind Storage,
        VideoFormat Format,
        GpuIdentity? Device
    );

    private sealed class Frames(int keep = 0) : IVideoFrameConsumer
    {
        private readonly Channel<Seen> _seen = Channel.CreateUnbounded<Seen>();
        private readonly List<VideoFrameLease> _kept = [];

        public int Kept
        {
            get
            {
                lock (_kept)
                {
                    return _kept.Count;
                }
            }
        }

        public void OnFrame(in VideoFrame frame)
        {
            lock (_kept)
            {
                if (_kept.Count < keep)
                {
                    _kept.Add(frame.Retain());
                }
            }

            _ = _seen.Writer.TryWrite(
                new Seen(frame.Storage.Kind, frame.Format, frame.Storage.Device)
            );
        }

        public async Task<Seen> NextAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(FrameWait);
            return await _seen.Reader.ReadAsync(timeout.Token);
        }

        // Reads frames until one arrives in the storage, as the reader is remade for the consumers.
        public async Task UntilAsync(VideoStorageKind storage, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 30; i++)
            {
                if ((await NextAsync(cancellationToken)).Storage == storage)
                {
                    return;
                }
            }

            Assert.Fail($"No {storage} frame in 30.");
        }

        public void ReleaseKept()
        {
            lock (_kept)
            {
                foreach (VideoFrameLease lease in _kept)
                {
                    lease.Dispose();
                }

                _kept.Clear();
            }
        }
    }
}
