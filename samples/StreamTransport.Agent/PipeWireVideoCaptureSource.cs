#if HAS_PIPEWIRE
using System.Diagnostics;
using Agash.StreamTransport;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PipeWire.NET;
using PwPixelFormat = PipeWire.NET.PixelFormat;
using PwVideoFrame = PipeWire.NET.VideoFrame;
using VideoFrame = Agash.StreamTransport.VideoFrame;

namespace StreamTransport.Agent;

/// <summary>
/// Captures a PipeWire video node (Linux - desktop/screen/app share, or the rockchip IRL camera) and
/// feeds it to the transport. PipeWire delivers packed BGRA/RGBA or planar YUV (I420/YUY2) frames either as host memory (MemPtr) or as a
/// DMA-BUF file descriptor. The MemPtr path converts to NV12 on the CPU and encodes through the VAAPI
/// path; the DMA-BUF path carries the fd as a PipeWire interop surface so the encoder can import it as a
/// VAAPI surface (DRM-PRIME) for a fully zero-copy pipeline. Pull-based: the PipeWire callback stores the
/// latest frame and <see cref="TryGetFrame"/> hands it out.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class PipeWireVideoCaptureSource : IVideoFrameSource, IAsyncDisposable
{
    private readonly PipeWireContext _context;
    private readonly PipeWireVideoCapture _capture;
    private readonly bool _alpha;
    private readonly Lock _gate = new();
    private byte[]? _nv12;
    private byte[]? _bgra;
    private int _width;
    private int _height;
    private long _timeNs;
    private bool _hasNew;

    private PipeWireVideoCaptureSource(PipeWireContext context, PipeWireVideoCapture capture, bool alpha)
    {
        _context = context;
        _capture = capture;
        _alpha = alpha;
        _capture.FrameReady += OnFrameReady;
    }

    /// <summary>Start the PipeWire loop and connect to <paramref name="targetNodeId"/> (or any node).</summary>
    public static async Task<PipeWireVideoCaptureSource> CreateAsync(uint targetNodeId, bool alpha = false, ILoggerFactory? loggerFactory = null)
    {
        var context = new PipeWireContext("StreamTransport.Agent", loggerFactory);
        await context.StartAsync().ConfigureAwait(false);
        var capture = new PipeWireVideoCapture(context);
        var source = new PipeWireVideoCaptureSource(context, capture, alpha);
        // Alpha needs a format that carries it (BGRA); otherwise accept the full set including YUV.
        PwPixelFormat[] formats = alpha
            ? [PwPixelFormat.Bgra]
            : [PwPixelFormat.Bgra, PwPixelFormat.Bgrx, PwPixelFormat.Rgba, PwPixelFormat.Yuv420, PwPixelFormat.Yuyv];
        capture.Connect(targetNodeId, formats);
        return source;
    }

    private void OnFrameReady(PipeWireVideoCapture sender, PwVideoFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }

        if (frame.BufferType == PipeWireBufferType.DmaBuf && frame.Data.IsEmpty)
        {
            // TODO(linux-zero-copy): import frame.Fd as a VAAPI surface via DRM-PRIME and encode it
            // directly. Until then, only mapped (MemPtr / mappable DMA-BUF) frames are converted below.
            return;
        }

        // MemPtr (or a mapped DMA-BUF): convert the mapped frame to NV12 on the CPU. Desktop/avatar sources
        // (OBS, VTube Studio, screen-share) deliver packed BGRA/RGBA; cameras and the IRL agent deliver YUV
        // (I420 / YUY2). Handle both so a webcam isn't silently dropped on a format mismatch.
        // Guard against a mapped buffer smaller than the pixel format implies (e.g. a planar format whose
        // planes arrive as separate data blocks) - skip the frame rather than read out of bounds.
        int requiredBytes = frame.Format switch
        {
            PwPixelFormat.Yuv420 => (frame.Stride * frame.Height) + (2 * ((frame.Stride / 2) * (frame.Height / 2))),
            PwPixelFormat.Yuyv => frame.Stride * frame.Height,
            _ => frame.Stride * frame.Height,
        };
        if (frame.Data.Length < requiredBytes)
        {
            return;
        }

        if (_alpha)
        {
            // Deliver BGRA (with its alpha) straight to the pipeline, which packs colour|alpha before encode.
            // Only BGRA carries alpha; the alpha capture requests only that format, so other formats are rare.
            if (frame.Format != PwPixelFormat.Bgra)
            {
                return;
            }

            byte[] bgra = new byte[frame.Width * frame.Height * 4];
            for (int y = 0; y < frame.Height; y++)
            {
                frame.Data.Slice(y * frame.Stride, frame.Width * 4).CopyTo(bgra.AsSpan(y * frame.Width * 4));
            }

            lock (_gate)
            {
                _bgra = bgra;
                _width = frame.Width;
                _height = frame.Height;
                _timeNs = NowNs();
                _hasNew = true;
            }

            return;
        }

        byte[] nv12 = new byte[frame.Width * frame.Height * 3 / 2];
        switch (frame.Format)
        {
            case PwPixelFormat.Yuv420:
                I420ToNv12(frame.Data, frame.Stride, frame.Width, frame.Height, nv12);
                break;
            case PwPixelFormat.Yuyv:
                YuyvToNv12(frame.Data, frame.Stride, frame.Width, frame.Height, nv12);
                break;
            default:
                PackedToNv12(frame.Data, frame.Stride, frame.Width, frame.Height, frame.Format, nv12);
                break;
        }
        lock (_gate)
        {
            _nv12 = nv12;
            _width = frame.Width;
            _height = frame.Height;
            _timeNs = NowNs();
            _hasNew = true;
        }
    }

    public bool TryGetFrame(out VideoFrame frame)
    {
        lock (_gate)
        {
            if (!_hasNew)
            {
                frame = default;
                return false;
            }

            _hasNew = false;
            byte[]? pixels = _alpha ? _bgra : _nv12;
            if (pixels is null)
            {
                frame = default;
                return false;
            }

            frame = VideoFrame.FromPixels(pixels, _alpha ? VideoPixelFormat.Bgra : VideoPixelFormat.Nv12, _width, _height, _timeNs);
            return true;
        }
    }

    private static void PackedToNv12(ReadOnlySpan<byte> src, int stride, int width, int height, PwPixelFormat format, byte[] nv12)
    {
        // Channel offsets within a 4-byte pixel for the supported packed formats.
        (int rIdx, int bIdx) = format == PwPixelFormat.Rgba ? (0, 2) : (2, 0); // BGRA/BGRx default
        int uvOffset = width * height;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int p = row + (x * 4);
                byte r = src[p + rIdx];
                byte g = src[p + 1];
                byte b = src[p + bIdx];
                nv12[(y * width) + x] = (byte)((((66 * r) + (129 * g) + (25 * b) + 128) >> 8) + 16);

                if ((y & 1) == 0 && (x & 1) == 0)
                {
                    int uv = uvOffset + ((y / 2) * width) + (x & ~1);
                    nv12[uv] = (byte)((((-38 * r) - (74 * g) + (112 * b) + 128) >> 8) + 128);
                    nv12[uv + 1] = (byte)((((112 * r) - (94 * g) - (18 * b) + 128) >> 8) + 128);
                }
            }
        }
    }

    // I420 (PipeWire Yuv420): three contiguous planes in the mapped buffer - Y (stride x height), then U and
    // V (each stride/2 x height/2). Repack into NV12 (full Y plane + interleaved UV); no colour conversion.
    private static void I420ToNv12(ReadOnlySpan<byte> src, int stride, int width, int height, byte[] nv12)
    {
        int chromaStride = stride / 2;
        int chromaWidth = width / 2;
        int chromaHeight = height / 2;
        int uPlane = stride * height;
        int vPlane = uPlane + (chromaStride * chromaHeight);
        int uvOffset = width * height;

        for (int y = 0; y < height; y++)
        {
            int srcRow = y * stride;
            int dstRow = y * width;
            for (int x = 0; x < width; x++)
            {
                nv12[dstRow + x] = src[srcRow + x];
            }
        }

        for (int cy = 0; cy < chromaHeight; cy++)
        {
            int uRow = uPlane + (cy * chromaStride);
            int vRow = vPlane + (cy * chromaStride);
            int dst = uvOffset + (cy * width);
            for (int cx = 0; cx < chromaWidth; cx++)
            {
                nv12[dst + (cx * 2)] = src[uRow + cx];
                nv12[dst + (cx * 2) + 1] = src[vRow + cx];
            }
        }
    }

    // YUY2 (PipeWire Yuyv): packed 4:2:2, each 4 bytes = Y0 U Y1 V (two pixels). Subsample 4:2:2 -> 4:2:0 by
    // taking chroma from even luma rows only.
    private static void YuyvToNv12(ReadOnlySpan<byte> src, int stride, int width, int height, byte[] nv12)
    {
        int uvOffset = width * height;
        for (int y = 0; y < height; y++)
        {
            int srcRow = y * stride;
            int dstRow = y * width;
            for (int x = 0; x < width; x += 2)
            {
                int p = srcRow + (x * 2);
                byte y0 = src[p];
                byte u = src[p + 1];
                byte y1 = src[p + 2];
                byte v = src[p + 3];
                nv12[dstRow + x] = y0;
                nv12[dstRow + x + 1] = y1;
                if ((y & 1) == 0)
                {
                    int uv = uvOffset + ((y / 2) * width) + x;
                    nv12[uv] = u;
                    nv12[uv + 1] = v;
                }
            }
        }
    }

    private static long NowNs() => Stopwatch.GetTimestamp() * (1_000_000_000L / Stopwatch.Frequency);

    public async ValueTask DisposeAsync()
    {
        await _capture.DisposeAsync().ConfigureAwait(false);
        await _context.DisposeAsync().ConfigureAwait(false);
    }
}
#endif
