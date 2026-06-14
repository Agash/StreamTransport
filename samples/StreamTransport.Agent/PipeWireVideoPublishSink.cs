#if HAS_PIPEWIRE
using System.Runtime.Versioning;
using Agash.StreamTransport;
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
        if (frame.Pixels.IsEmpty)
        {
            // A GPU-surface frame carries no CPU pixels; the host-memory path only serves decoded CPU frames.
            // The dmabuf zero-copy publish handles surface frames separately (see class remarks).
            return;
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
                var output = new PipeWireVideoOutput(_context, _nodeName, width, height, PwPixelFormat.Bgra, _frameRate);
                output.FillFrame += OnFillFrame;
                output.Connect();
                _output = output;
            }
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

        if (output is not null)
        {
            await output.DisposeAsync().ConfigureAwait(false);
        }

        await _context.DisposeAsync().ConfigureAwait(false);
    }
}
#endif
