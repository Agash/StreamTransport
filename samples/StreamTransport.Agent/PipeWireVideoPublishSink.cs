#if HAS_PIPEWIRE
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using Agash.StreamTransport;
using Agash.StreamTransport.Codecs;
using PipeWire.NET;
using PwPixelFormat = PipeWire.NET.PixelFormat;
using VideoFrame = Agash.StreamTransport.VideoFrame;

namespace StreamTransport.Agent;

/// <summary>
/// Publishes decoded frames to a PipeWire node (Linux) so downstream apps (OBS via <c>pipewiresrc</c>) can pick
/// them up - the Linux mirror of <c>SpoutVideoPublishSink</c> (Windows) and <c>SyphonVideoPublishSink</c> (macOS).
/// PipeWire is a <i>pull</i> producer: it invokes <see cref="PipeWireVideoOutput.FillFrame"/> on its loop thread
/// when it wants a frame, whereas the decoder <i>pushes</i> via <see cref="Submit"/>. So this keeps the latest
/// decoded frame and serves it on every pull (a virtual camera holding its last frame), which also decouples the
/// decode rate from the consumer's pull rate.
///
/// <para>This is the host-memory (CPU) publish path: the Linux receive pipeline decodes to CPU NV12/I420 (or
/// BGRA after a side-by-side-alpha unpack), and this converts to BGRA - the format OBS and compositors want -
/// once per frame. The zero-copy dmabuf publish (decoder emits a VAAPI/Vulkan surface, handed straight to
/// <see cref="PipeWireVideoOutput.ConnectDmaBuf"/>) is the GPU upgrade layered on top; the sink shape (lazy init
/// from the first frame, <see cref="SetPreserveAlpha"/>, latest-frame serve) is identical for both.</para>
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class PipeWireVideoPublishSink : IVideoFrameSink, IAsyncDisposable
{
    private readonly PipeWireContext _context;
    private readonly string _nodeName;
    private readonly int _frameRate;
    private readonly Lock _gate = new();
    private volatile bool _alpha;

    private PipeWireVideoOutput? _output;
    private byte[]? _bgra;     // latest decoded frame, tightly packed W*4 BGRA
    private int _width;
    private int _height;
    private bool _hasFrame;
    private bool _disposed;

    private static readonly bool s_debug = Environment.GetEnvironmentVariable("STX_PW_DEBUG") is { Length: > 0 };
    private long _submits;
    private long _fills;
    private long _served;

    // GPU zero-copy path (dmabuf frames): a pool of owned VAAPI surfaces exported to stable dmabufs (the
    // PipeWire buffers), plus one staging surface. Each decoded surface is VPP-copied into staging (Submit,
    // decode thread), then staging is VPP-copied into the PipeWire buffer the daemon chose (FillDmaBuf, loop
    // thread). Both copies are GPU blits guarded by _gate, so no CPU transfer and no tearing. See
    // VaapiPresentationPool and the zero-copy plan note for why VPP (radeonsi lacks vaCopy).
    private const int MaxDmaBufBuffers = 16;
    private VaapiPresentationPool? _pool;
    private int _stagingIndex;
    private bool _hasGpuFrame;
    private bool _gpuUnavailable;
    private Mode _mode;

    // Alpha GPU path: the decoded surface is the 2W x H side-by-side packed NV12; the staging surface holds it
    // and a Vulkan compute shader unpacks colour|alpha into W x H BGRA pool images that PipeWire serves. The
    // VAAPI pool is staging-only here (one surface); the Vulkan images are the PipeWire buffers. This is the
    // zero-copy receive mirror of the send-side pack - peer of D3D11AlphaUnpacker / the macOS Metal unpacker.
    private bool _alphaGpu;
    private int _outWidth;
    private int _outHeight;
    private VulkanComputeContext? _vk;
    private VulkanAlphaCodec? _alphaCodec;
    private VulkanAlphaCodec.ExportedImage[]? _vkOut;

    // --verify GPU mode: read the published surface back to CPU and hand it to the verification report, so the
    // actual zero-copy output (content + alpha gradient + sync markers) is checked exactly like the CPU path.
    private Action<VideoFrame>? _verifyTap;

    /// <summary>Enable content verification of the published GPU frames (CPU readback -&gt; <paramref name="tap"/>).</summary>
    public void EnableVerification(Action<VideoFrame> tap) => _verifyTap = tap;

    private static long NowNs() => (long)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency));

    private enum Mode { Unset, HostMemory, GpuDmaBuf }

    private PipeWireVideoPublishSink(PipeWireContext context, string nodeName, bool alpha, int frameRate)
    {
        _context = context;
        _nodeName = nodeName;
        _alpha = alpha;
        _frameRate = frameRate;
    }

    /// <summary>Start the PipeWire loop and return a sink ready to publish under <paramref name="nodeName"/>.</summary>
    public static async Task<PipeWireVideoPublishSink> CreateAsync(string nodeName, bool alpha = false, int frameRate = 30)
    {
        var context = new PipeWireContext();
        await context.StartAsync().ConfigureAwait(false);
        return new PipeWireVideoPublishSink(context, nodeName, alpha, frameRate);
    }

    /// <summary>
    /// Adopt the publisher's negotiated side-by-side-alpha setting. Safe to call before the first frame (the
    /// decoded BGRA vs NV12 decision is made per frame), so the receiver needs no flag of its own.
    /// </summary>
    public void SetPreserveAlpha(bool value) => _alpha = value;

    public void Submit(VideoFrame frame)
    {
        // GPU zero-copy path: a VAAPI DMA-BUF surface frame (the HW decoder kept it on the GPU). VPP-copy the
        // decoded surface into our staging surface now, while it is valid (the decoder recycles it on the next
        // decode). The PipeWire publish happens later from the staging surface (FillDmaBuf).
        if (frame.InteropKind == StreamInteropKind.PipeWire && frame.DmaBuf is not null && !_gpuUnavailable)
        {
            SubmitGpu(frame);
            return;
        }

        if (frame.Pixels.IsEmpty)
        {
            return; // a GPU-surface frame with no usable dmabuf and no CPU pixels: nothing to publish.
        }

        ReadOnlySpan<byte> px = frame.Pixels.Span;
        int width = frame.Width;
        int height = frame.Height;
        byte[] bgra = new byte[width * height * 4];

        switch (frame.PixelFormat)
        {
            case VideoPixelFormat.Bgra:
                // Already BGRA (the alpha path unpacked colour|alpha to W x H BGRA): copy as-is.
                px[..Math.Min(px.Length, bgra.Length)].CopyTo(bgra);
                break;
            case VideoPixelFormat.Nv12:
                Nv12ToBgra(px, width, height, bgra);
                break;
            case VideoPixelFormat.I420:
                I420ToBgra(px, width, height, bgra);
                break;
            default:
                return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _bgra = bgra;
            _width = width;
            _height = height;
            _hasFrame = true;
            if (s_debug && ++_submits <= 3)
            {
                Console.Error.WriteLine($"[pw-sink] submit#{_submits} {width}x{height} {frame.PixelFormat} bytes={bgra.Length}");
            }

            // Create the output lazily on the first frame, when the decoded dimensions are known. Construction
            // and Connect are synchronous (only the context start was async), so this needs no blocking wait.
            if (_output is null)
            {
                _mode = Mode.HostMemory;
                var output = new PipeWireVideoOutput(_context, _nodeName, width, height, PwPixelFormat.Bgra, _frameRate);
                output.FillFrame += OnFillFrame;
                output.Connect();
                _output = output;
            }
        }
    }

    // GPU path: VPP-copy the decoded VAAPI surface into our staging surface (decode thread, surface valid now).
    // On the first frame, build the presentation pool + the dmabuf PipeWire output. Both VPP copies are guarded
    // by _gate, which serialises this with the FillDmaBuf copy so the staging surface is never read mid-write.
    private void SubmitGpu(VideoFrame frame)
    {
        uint decodedSurfaceId = (uint)frame.Surface;
        if (decodedSurfaceId == 0)
        {
            return; // no VA surface id carried; cannot VPP-copy.
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_pool is null)
            {
                if (_mode == Mode.HostMemory)
                {
                    return; // already committed to the CPU path this session; ignore a stray surface frame.
                }

                try
                {
                    _alphaGpu = _alpha;
                    _mode = Mode.GpuDmaBuf;
                    PipeWireVideoOutput output;

                    if (_alphaGpu)
                    {
                        // Alpha: VAAPI pool is staging only (the decoded 2W x H NV12); Vulkan owns the W x H BGRA
                        // PipeWire buffers. Output node is BGRA at half the decoded width (colour|alpha -> colour).
                        _pool = new VaapiPresentationPool(frame.Width, frame.Height, 1);
                        _stagingIndex = 0;
                        _outWidth = frame.Width / 2;
                        _outHeight = frame.Height;
                        _vk = new VulkanComputeContext();
                        _alphaCodec = new VulkanAlphaCodec(_vk);
                        // Let the driver pick a tiled (gst-importable) BGRA modifier; advertise the one it chose.
                        // radeonsi's GL/EGL import rejects LINEAR, so a LINEAR-only offer fails to negotiate.
                        ulong[] candidates = _alphaCodec.OutputModifiers();
                        _vkOut = new VulkanAlphaCodec.ExportedImage[MaxDmaBufBuffers];
                        for (int i = 0; i < _vkOut.Length; i++)
                        {
                            _vkOut[i] = _alphaCodec.CreateOutputImage(_outWidth, _outHeight, candidates);
                        }

                        ulong outModifier = _vkOut[0].Modifier;
                        _width = _outWidth;
                        _height = _outHeight;
                        if (s_debug)
                        {
                            DmaBufSurface st = _pool.Planes(0);
                            Console.Error.WriteLine($"[pw-sink] alpha staging modifier=0x{_pool.Modifier:x} planes={st.PlaneCount} out={_outWidth}x{_outHeight} bgra fd0={_vkOut[0].Fd} pitch={_vkOut[0].RowPitch} cand=[{string.Join(",", candidates.Select(m => "0x" + m.ToString("x")))}] chosen=0x{outModifier:x}");
                        }

                        output = new PipeWireVideoOutput(_context, _nodeName, _outWidth, _outHeight, PwPixelFormat.Bgra, _frameRate);
                        output.AllocateDmaBuf += OnAllocateDmaBuf;
                        output.FillDmaBuf += OnFillDmaBuf;
                        if (s_debug)
                        {
                            output.StateChanged += (_, oldS, newS) => Console.Error.WriteLine($"[pw-sink] alpha-gpu state {oldS}->{newS}");
                        }

                        output.ConnectDmaBuf([(long)outModifier]);
                        _output = output;
                    }
                    else
                    {
                        _pool = new VaapiPresentationPool(frame.Width, frame.Height, MaxDmaBufBuffers + 1);
                        _stagingIndex = MaxDmaBufBuffers; // the last surface is staging; 0..Max-1 are PipeWire buffers.
                        if (s_debug)
                        {
                            DmaBufSurface s0 = _pool.Planes(0);
                            Console.Error.WriteLine($"[pw-sink] pool modifier=0x{_pool.Modifier:x} planes={s0.PlaneCount} p0(fd={s0[0].Fd},off={s0[0].Offset},stride={s0[0].Stride},fourcc=0x{s0[0].DrmFourcc:x})");
                        }
                        _width = frame.Width;
                        _height = frame.Height;

                        // --verify only: a Vulkan codec to read the NV12 staging surface back to CPU (libva's
                        // vaGetImage/vaDeriveImage abort inside the radeonsi driver for these tiled surfaces).
                        if (_verifyTap is not null)
                        {
                            _vk = new VulkanComputeContext();
                            _alphaCodec = new VulkanAlphaCodec(_vk);
                        }

                        output = new PipeWireVideoOutput(_context, _nodeName, frame.Width, frame.Height, PwPixelFormat.Nv12, _frameRate);
                        output.AllocateDmaBuf += OnAllocateDmaBuf;
                        output.FillDmaBuf += OnFillDmaBuf;
                        if (s_debug)
                        {
                            output.StateChanged += (_, oldS, newS) => Console.Error.WriteLine($"[pw-sink] gpu state {oldS}->{newS}");
                        }

                        output.ConnectDmaBuf([(long)_pool.Modifier]);
                        _output = output;
                    }
                }
                catch (Exception ex)
                {
                    // No usable VAAPI presentation pool: stop attempting the GPU path (frames will be dropped
                    // rather than crashing the decode thread). The CPU fallback only applies if the decoder
                    // also emits CPU frames, which a GPU-surface decoder does not.
                    _gpuUnavailable = true;
                    DisposeGpuResources();
                    if (s_debug)
                    {
                        Console.Error.WriteLine($"[pw-sink] GPU pool unavailable: {ex.Message}");
                    }

                    return;
                }
            }

            int rc = _pool.CopyInto(decodedSurfaceId, _stagingIndex);
            if (rc == 0)
            {
                _hasGpuFrame = true;
                // Do not drive the graph from this decode thread: pw_stream_trigger_process must run on the
                // PipeWire loop thread, and calling it here crashes inside libpipewire's loop. The consumer
                // (gst/OBS) drives the cycle; FillDmaBuf then pulls the latest staged frame.
                if (s_debug && ++_submits <= 3)
                {
                    Console.Error.WriteLine($"[pw-sink] gpu submit#{_submits} {frame.Width}x{frame.Height} srcSurf={decodedSurfaceId}");
                }

                // --verify: read the just-produced GPU surface back to CPU and feed the verification report.
                // Done here (per decoded frame) rather than in FillDmaBuf so it runs even with no consumer
                // pulling. Mirrors exactly what the zero-copy path publishes: opaque = the VPP'd NV12 staging;
                // alpha = the Vulkan unpack of that staging into a BGRA pool image.
                if (_verifyTap is not null)
                {
                    EmitVerifyFrame();
                }
            }
            else if (s_debug)
            {
                Console.Error.WriteLine($"[pw-sink] gpu VPP decoded->staging failed VAStatus={rc}");
            }
        }
    }

    // PipeWire negotiated dmabuf buffers and asks us to back buffer `bufferIndex`: hand it the stable planes of
    // pool surface `bufferIndex`. The top pool surface is reserved for staging, so decline indices at/above it.
    private int OnAllocateDmaBuf(PipeWireVideoOutput sender, int bufferIndex, int width, int height, ulong modifier, Span<VideoPlane> planes)
    {
        lock (_gate)
        {
            if (_alphaGpu)
            {
                // Alpha: hand PipeWire the stable single-plane BGRA dmabuf of Vulkan output image `bufferIndex`.
                if (_vkOut is null || bufferIndex >= _vkOut.Length || planes.IsEmpty)
                {
                    return 0;
                }

                VulkanAlphaCodec.ExportedImage img = _vkOut[bufferIndex];
                uint size = (uint)(img.RowPitch * (ulong)_outHeight);
                planes[0] = new VideoPlane(img.Fd, (uint)img.Offset, (int)img.RowPitch, size);
                if (s_debug)
                {
                    Console.Error.WriteLine($"[pw-sink] alpha allocDmaBuf buf={bufferIndex} fd={img.Fd} off={img.Offset} pitch={img.RowPitch} size={size}");
                }

                return 1;
            }

            if (_pool is null || bufferIndex >= _stagingIndex)
            {
                return 0;
            }

            DmaBufSurface surface = _pool.Planes(bufferIndex);
            int n = Math.Min(surface.PlaneCount, planes.Length);
            if (s_debug)
            {
                Console.Error.WriteLine($"[pw-sink] allocDmaBuf buf={bufferIndex} planes={surface.PlaneCount} modifier=0x{modifier:x} stagingIdx={_stagingIndex}");
            }
            for (int p = 0; p < n; p++)
            {
                DmaBufPlane pl = surface[p];
                // NV12 plane byte extents: Y is stride*height, the interleaved UV plane is stride*(height/2).
                uint size = (uint)((int)pl.Stride * (p == 0 ? _height : _height / 2));
                planes[p] = new VideoPlane(pl.Fd, pl.Offset, (int)pl.Stride, size);
            }

            return n;
        }
    }

    // PipeWire pulls a dmabuf frame into the buffer it chose (`bufferIndex`): VPP-copy the latest staged frame
    // into that buffer's surface and publish it. Runs on the loop thread; _gate serialises with SubmitGpu.
    private bool OnFillDmaBuf(PipeWireVideoOutput sender, int bufferIndex)
    {
        lock (_gate)
        {
            if (_disposed || _pool is null || !_hasGpuFrame)
            {
                return false;
            }

            if (_alphaGpu)
            {
                // Unpack the staged 2W x H NV12 (its dmabuf planes) into Vulkan output image `bufferIndex` (BGRA).
                if (_alphaCodec is null || _vkOut is null || bufferIndex >= _vkOut.Length)
                {
                    return false;
                }

                DmaBufSurface st = _pool.Planes(_stagingIndex);
                if (st.PlaneCount < 2)
                {
                    return false;
                }

                DmaBufPlane y = st[0];
                DmaBufPlane uv = st[1];
                try
                {
                    _alphaCodec.UnpackInto(
                        new VulkanAlphaCodec.Nv12Plane(y.Fd, y.Offset, y.Stride),
                        new VulkanAlphaCodec.Nv12Plane(uv.Fd, uv.Offset, uv.Stride),
                        _outWidth, _outHeight, _pool.Modifier, _vkOut[bufferIndex]);
                }
                catch (Exception ex)
                {
                    if (s_debug && _served < 3)
                    {
                        Console.Error.WriteLine($"[pw-sink] alpha UnpackInto FAILED: {ex.GetType().Name}: {ex.Message}");
                        _served++;
                    }

                    return false;
                }

                if (s_debug && ++_served <= 3)
                {
                    Console.Error.WriteLine($"[pw-sink] alpha served#{_served} -> buffer {bufferIndex} ({_outWidth}x{_outHeight})");
                }

                return true;
            }

            if (bufferIndex >= _stagingIndex)
            {
                return false;
            }

            int rc = _pool.CopyInto(_pool.SurfaceId(_stagingIndex), bufferIndex);
            if (s_debug && ++_served <= 3)
            {
                Console.Error.WriteLine($"[pw-sink] gpu served#{_served} -> buffer {bufferIndex} VAStatus={rc}");
            }

            return rc == 0;
        }
    }

    // PipeWire pulls a host-memory frame: copy the latest decoded BGRA into the buffer it gave us and publish it
    // (return true). With no frame yet, emit nothing (return false). Runs on the PipeWire loop thread.
    private bool OnFillFrame(PipeWireVideoOutput sender, Span<byte> pixels, int stride, int width, int height, PwPixelFormat format)
    {
        lock (_gate)
        {
            if (s_debug && ++_fills <= 3)
            {
                Console.Error.WriteLine($"[pw-sink] fill#{_fills} want {width}x{height} stride={stride} have {_width}x{_height} hasFrame={_hasFrame}");
            }

            if (!_hasFrame || _bgra is null || width != _width || height != _height)
            {
                return false;
            }

            if (s_debug && ++_served <= 3)
            {
                Console.Error.WriteLine($"[pw-sink] served#{_served}");
            }

            int srcStride = _width * 4;
            for (int y = 0; y < height; y++)
            {
                int srcRow = y * srcStride;
                int dstRow = y * stride;
                int copy = Math.Min(srcStride, stride);
                _bgra.AsSpan(srcRow, copy).CopyTo(pixels.Slice(dstRow, copy));
            }

            return true;
        }
    }

    // NV12 (Y plane then interleaved UV, 4:2:0) -> BGRA, BT.709 limited -> full range. Matches the constants in
    // AlphaPacking.UnpackNv12ToBgra and the GPU NV12->BGRA shaders so colour is identical across publish paths.
    private static void Nv12ToBgra(ReadOnlySpan<byte> nv12, int width, int height, byte[] bgra)
    {
        int uvOffset = width * height;
        for (int y = 0; y < height; y++)
        {
            int yRow = y * width;
            int uvRow = uvOffset + ((y / 2) * width);
            int dstRow = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int yy = nv12[yRow + x] - 16;
                int cb = nv12[uvRow + (x & ~1)] - 128;
                int cr = nv12[uvRow + (x & ~1) + 1] - 128;
                WriteBgra(bgra, dstRow + (x * 4), yy, cb, cr);
            }
        }
    }

    // I420 (separate Y, U, V planes, 4:2:0) -> BGRA. A software HEVC decoder emits I420; same colour matrix.
    private static void I420ToBgra(ReadOnlySpan<byte> i420, int width, int height, byte[] bgra)
    {
        int uPlane = width * height;
        int vPlane = uPlane + (width * height / 4);
        int chromaStride = width / 2;
        for (int y = 0; y < height; y++)
        {
            int yRow = y * width;
            int cRow = (y / 2) * chromaStride;
            int dstRow = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int yy = i420[yRow + x] - 16;
                int cb = i420[uPlane + cRow + (x / 2)] - 128;
                int cr = i420[vPlane + cRow + (x / 2)] - 128;
                WriteBgra(bgra, dstRow + (x * 4), yy, cb, cr);
            }
        }
    }

    private static void WriteBgra(byte[] bgra, int d, int yy, int cb, int cr)
    {
        int r = ((298 * yy) + (459 * cr)) >> 8;
        int g = ((298 * yy) - (55 * cb) - (136 * cr)) >> 8;
        int b = ((298 * yy) + (541 * cb)) >> 8;
        bgra[d] = Clamp(b);
        bgra[d + 1] = Clamp(g);
        bgra[d + 2] = Clamp(r);
        bgra[d + 3] = 255;
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    // Read the just-staged GPU surface back to CPU and hand it to the verification tap. Caller holds _gate.
    // Opaque: the VPP'd NV12 staging surface. Alpha: unpack that staging into a BGRA pool image (no consumer
    // contends in verify mode) and read that back. Exactly the pixels the zero-copy path would publish.
    private void EmitVerifyFrame()
    {
        // Stamp the delivery (playout) time NOW, before the synchronous readback below - the readback adds
        // latency, and timing the marker after it would inflate the measured A/V skew. VerifyingVideoSink reads
        // this off the frame's PresentationTimeNs (usePresentationTime: true) instead of its own receipt clock.
        long obsNs = NowNs();
        try
        {
            if (_alphaGpu)
            {
                if (_alphaCodec is null || _vkOut is null || _pool is null)
                {
                    return;
                }

                DmaBufSurface st = _pool.Planes(_stagingIndex);
                if (st.PlaneCount < 2)
                {
                    return;
                }

                DmaBufPlane y = st[0];
                DmaBufPlane uv = st[1];
                _alphaCodec.UnpackInto(
                    new VulkanAlphaCodec.Nv12Plane(y.Fd, y.Offset, y.Stride),
                    new VulkanAlphaCodec.Nv12Plane(uv.Fd, uv.Offset, uv.Stride),
                    _outWidth, _outHeight, _pool.Modifier, _vkOut[0]);
                byte[] bgra = _alphaCodec.ReadbackToBgra(_vkOut[0], _outWidth, _outHeight);
                _verifyTap!(VideoFrame.FromPixels(bgra, VideoPixelFormat.Bgra, _outWidth, _outHeight, obsNs));
            }
            else if (_pool is not null && _alphaCodec is not null)
            {
                DmaBufSurface st = _pool.Planes(_stagingIndex);
                if (st.PlaneCount >= 2)
                {
                    DmaBufPlane y = st[0];
                    DmaBufPlane uv = st[1];
                    byte[] nv12 = _alphaCodec.ReadbackNv12(
                        new VulkanAlphaCodec.Nv12Plane(y.Fd, y.Offset, y.Stride),
                        new VulkanAlphaCodec.Nv12Plane(uv.Fd, uv.Offset, uv.Stride),
                        _width, _height, _pool.Modifier);
                    _verifyTap!(VideoFrame.FromPixels(nv12, VideoPixelFormat.Nv12, _width, _height, obsNs));
                }
            }
        }
        catch (Exception ex)
        {
            if (s_debug)
            {
                Console.Error.WriteLine($"[pw-sink] verify readback failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // Free the GPU republish resources (VAAPI pool + Vulkan codec/device + exported output images). Caller holds
    // _gate. Used by the init-failure path; DisposeAsync does the same teardown after stopping the stream.
    private void DisposeGpuResources()
    {
        if (_alphaCodec is not null && _vkOut is not null)
        {
            foreach (VulkanAlphaCodec.ExportedImage img in _vkOut)
            {
                _alphaCodec.DestroyExported(img);
            }
        }

        _vkOut = null;
        _alphaCodec?.Dispose();
        _alphaCodec = null;
        _vk?.Dispose();
        _vk = null;
        _pool?.Dispose();
        _pool = null;
    }

    public async ValueTask DisposeAsync()
    {
        PipeWireVideoOutput? output;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            output = _output;
            _output = null;
        }

        // Stop the PipeWire stream first (it releases the dmabuf buffers it imported from the pool / Vulkan output
        // images), then free the GPU resources, so the daemon never references a freed surface.
        if (output is not null)
        {
            await output.DisposeAsync().ConfigureAwait(false);
        }

        lock (_gate)
        {
            DisposeGpuResources();
        }

        await _context.DisposeAsync().ConfigureAwait(false);
    }
}
#endif
