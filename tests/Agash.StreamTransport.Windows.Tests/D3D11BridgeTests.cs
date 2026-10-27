using Agash.StreamTransport.Media;
using Agash.StreamTransport.Windows.Direct3D12;
using FFmpeg.Interop.Native;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Windows.Tests;

[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class D3D11BridgeTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SharedTexture_OpensOnTheNativeDevice(bool nv12)
    {
        var format = nv12 ? DXGI_FORMAT.DXGI_FORMAT_NV12 : DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        using var gpu = TestGpu.Open();
        using var engine = new D3D12Engine(gpu.Device);
        var bridge = D3D11Bridge.For(engine.Adapter);
        nint texture = engine.CreateTexture(
            format,
            256,
            256,
            false,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
            shared: true
        );
        try
        {
            nint opened = bridge.OpenTexture(engine.Device, texture);
            Assert.AreNotEqual(0, opened);
            D3D11Bridge.Release(opened);
        }
        finally
        {
            D3D12Engine.Release(texture);
        }
    }

    [TestMethod]
    public unsafe void Nv12ForDirect3D11_ReachesAnEncoderPoolThroughTheFences()
    {
        // A grey picture, processed on Direct3D 12 for a consumer on a native Direct3D 11 device, is
        // copied into an FFmpeg Direct3D 11 pool the way the NVENC and AMF encoders take it: after a GPU
        // wait for the processor's fence. Mid grey is luma 126 and neutral chroma in BT.709 video range.
        const int width = 64;
        const int height = 32;
        using var gpu = TestGpu.Open();
        byte[] pixels = new byte[width * height * 4];
        pixels.AsSpan().Fill(128);
        nint source = gpu.Upload(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, width, height, pixels);
        D3D12VideoProcessorFactory factory = new();
        using IVideoProcessor processor = factory.Create(
            new VideoStreamDescription(
                VideoStorageKind.D3D12,
                PixelFormat.Bgra,
                new VideoSize(width, height),
                gpu.Adapter
            ),
            new VideoProcessing(
                new VideoConstraints([VideoStorageKind.D3D11], [PixelFormat.Nv12], gpu.Adapter)
            )
        );
        Assert.AreEqual(VideoStorageKind.D3D11, processor.Info.Output.Storage);

        PoolCopy copy = new(width, height);
        for (int i = 0; i < 3; i++)
        {
            processor.Process(
                new VideoFrame(
                    new VideoStorage(new D3D12Image(source, 0, gpu.Adapter)),
                    new VideoFormat(PixelFormat.Bgra, width, height),
                    MediaTimestamp.Captured(new MediaTime(i)),
                    color: VideoColor.Srgb,
                    retainer: new NotRetained()
                ),
                copy
            );
        }

        Assert.AreEqual(3, copy.Copied);
        using FF.Frame memory = new();
        copy.Last!.TransferTo(memory);
        FF.ReadOnlyImagePlane luma = memory.GetPlane(0);
        FF.ReadOnlyImagePlane chroma = memory.GetPlane(1);
        for (int row = 0; row < height; row++)
        {
            foreach (byte y in luma.GetRow(row))
            {
                Assert.AreEqual(126, y, 1);
            }
        }

        foreach (byte c in chroma.GetRow(0))
        {
            Assert.AreEqual(128, c, 1);
        }

        copy.Dispose();
    }

    // Takes each frame as D3D11Input does for a fenced texture: a GPU-ordered copy into its own pool.
    private sealed unsafe class PoolCopy(int width, int height) : IVideoFrameConsumer, IDisposable
    {
        private FF.HardwareDevice? _device;
        private FF.HardwareFramePool? _pool;

        public int Copied { get; private set; }

        public FF.Frame? Last { get; private set; }

        public void OnFrame(in VideoFrame frame)
        {
            Assert.IsTrue(frame.Storage.TryGetValue(out D3D11Image image));
            Assert.AreEqual(D3D11SyncKind.Fence, image.Sync.Kind);
            _device ??= FF.D3D11VAExtensions.FromD3D11Texture(image.Texture);
            _pool ??= FF.HardwareFramePool.Create(
                _device,
                FF.PixelFormat.D3D11,
                FF.PixelFormat.Nv12,
                width,
                height,
                initialSize: 4,
                configure: pool => ((AVD3D11VAFramesContext*)pool.Context->hwctx)->BindFlags = 0x200
            );
            Last?.Dispose();
            Last = new FF.Frame();
            FF.D3D11VAExtensions.CopyFromD3D11Texture(
                _pool,
                image.Texture,
                image.Subresource,
                Last,
                new FF.D3D11FenceSync(image.Sync.Fence, image.Sync.Value)
            );
            Copied++;
        }

        public void Dispose()
        {
            Last?.Dispose();
            _pool?.Dispose();
            _device?.Dispose();
        }
    }
}
