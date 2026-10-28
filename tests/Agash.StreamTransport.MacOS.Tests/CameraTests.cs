using System.Collections.Concurrent;
using System.Collections.Immutable;
using Agash.StreamTransport.MacOS.AVFoundation;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.MacOS.Tests;

/// <summary>A camera through AVFoundation; needs a camera, a virtual one included.</summary>
[TestClass]
[TestCategory("Integration")]
public sealed class CameraTests
{
    public TestContext TestContext { get; set; } = null!;

    // A kept camera frame holds its pixel buffer, as VideoToolbox holds what it encodes: while held, its
    // surface is never handed out again, and the camera keeps delivering from its other buffers.
    [TestMethod]
    [Timeout(60_000)]
    public async Task KeptFrames_HoldTheirSurfacesWhileTheCameraRuns()
    {
        AVFoundationVideoInputProvider provider = new();
        ImmutableArray<VideoInputInfo> inputs = await provider.GetInputsAsync(
            TestContext.CancellationToken
        );
        if (inputs.IsEmpty)
        {
            Assert.Inconclusive("No camera on this machine.");
        }

        IVideoInput input;
        try
        {
            input = await provider.OpenAsync(
                inputs[0],
                new VideoInputRequest(),
                TestContext.CancellationToken
            );
        }
        catch (UnauthorizedAccessException denied)
        {
            Assert.Inconclusive(denied.Message);
            return;
        }

        using (input)
        {
            await KeepFramesAsync(input);
        }
    }

    private async Task KeepFramesAsync(IVideoInput input)
    {
        Holder holder = new(inFlight: 3, wanted: 20);
        using (
            input.Connect(
                holder,
                new VideoConstraints(
                    [VideoStorageKind.IOSurface],
                    [PixelFormat.Nv12, PixelFormat.Bgra],
                    TestSurfaces.Device
                )
            )
        )
        {
            await holder.Done.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
        }

        holder.Release();
        Assert.AreEqual(0, holder.Reused, "a kept frame's surface was handed out again while held");
    }

    // Keeps the last few frames, as an encoder with frames in flight does.
    private sealed class Holder(int inFlight, int wanted) : IVideoFrameConsumer
    {
        private readonly ConcurrentQueue<(nint Surface, VideoFrameLease Lease)> _held = new();
        private readonly TaskCompletionSource _done = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _seen;

        public Task Done => _done.Task;

        public int Reused { get; private set; }

        public void OnFrame(in VideoFrame frame)
        {
            if (_done.Task.IsCompleted || !frame.Storage.TryGetValue(out IOSurfaceImage image))
            {
                return;
            }

            if (_held.Any(h => h.Surface == image.Surface))
            {
                Reused++;
            }

            _held.Enqueue((image.Surface, frame.Retain()));
            while (_held.Count > inFlight && _held.TryDequeue(out var oldest))
            {
                oldest.Lease.Dispose();
            }

            if (++_seen >= wanted)
            {
                _done.TrySetResult();
            }
        }

        public void Release()
        {
            while (_held.TryDequeue(out var held))
            {
                held.Lease.Dispose();
            }
        }
    }
}
