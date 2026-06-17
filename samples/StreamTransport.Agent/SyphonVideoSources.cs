#if HAS_SYPHON
using System.Diagnostics;
using System.Runtime.Versioning;
using Agash.StreamTransport;
using Agash.StreamTransport.Codecs;
using CoreVideo;
using IOSurface;
using ObjCRuntime;
using Syphon.NET;

namespace StreamTransport.Agent;

/// <summary>
/// Captures a Syphon server's shared IOSurface (macOS) and feeds it to the transport as a BGRA surface
/// frame - true zero-copy. Syphon surfaces are BGRA and VideoToolbox encodes BGRA directly (in-ASIC
/// conversion), so there is no conversion pass on capture. macOS-only.
/// </summary>
/// <remarks>
/// A console process has no Cocoa run loop, so all directory and client interaction is funnelled onto a
/// single owned thread that pumps the run loop (<see cref="SyphonServerDirectory.PumpEvents"/>); the
/// directory's documentation requires discovery and frame notifications to be driven from the thread
/// that created it. Received <see cref="SyphonFrame"/>s retain their backing surface, so the latest two
/// frames are kept alive (double-buffered) until superseded, bounding retention while leaving the
/// surface valid for the encoder that consumes the handle.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class SyphonVideoCaptureSource : IVideoFrameSource, IDisposable
{
    private readonly string? _serverName;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Lock _gate = new();
    private readonly IAlphaPacker? _alpha;
    private volatile bool _disposed;
    private Exception? _connectError;
    private nint _surface;
    private int _width;
    private int _height;
    private long _timeNs;
    private bool _hasNew;

    private SyphonVideoCaptureSource(string? serverName, bool alpha)
    {
        _serverName = serverName;
        _alpha = alpha ? new MetalAlphaCodec() : null;
        _thread = new Thread(Run) { IsBackground = true, Name = "syphon-capture" };
        _thread.Start();
    }

    /// <summary>
    /// Discover a Syphon server through the shared directory and connect to it zero-copy. When
    /// <paramref name="serverName"/> is given, the first server whose server or app name contains it
    /// (case-insensitive) is chosen; otherwise the first advertised server is used. Blocks until a server
    /// is found or <paramref name="timeout"/> elapses. <paramref name="alpha"/> preserves transparency by
    /// packing each captured IOSurface side-by-side (colour|alpha) on the GPU via <see cref="MetalAlphaCodec"/>.
    /// </summary>
    public static SyphonVideoCaptureSource Connect(string? serverName, bool alpha = false, TimeSpan? timeout = null)
    {
        var source = new SyphonVideoCaptureSource(serverName, alpha);
        if (!source._ready.Wait(timeout ?? TimeSpan.FromSeconds(8)) || source._connectError is not null)
        {
            Exception? error = source._connectError;
            source.Dispose();
            throw error ?? new InvalidOperationException("Timed out waiting for a Syphon server to appear.");
        }

        return source;
    }

    public bool TryGetFrame(out VideoFrame frame)
    {
        lock (_gate)
        {
            if (!_hasNew || _surface == 0)
            {
                frame = default;
                return false;
            }

            _hasNew = false;
            if (_alpha is not null)
            {
                // GPU-pack the captured BGRA surface into a 2W x H colour|alpha surface and emit that;
                // VideoToolbox then encodes it BGRA-direct. Driven through IAlphaPacker (same as the Spout
                // source). Packing under the lock keeps the source surface alive for the sub-ms Metal pass.
                var colour = VideoFrame.FromSurface(_surface, StreamInteropKind.Syphon, _width, _height, _timeNs);
                frame = _alpha.PackAlpha(colour, _timeNs);
                return true;
            }

            frame = VideoFrame.FromSurface(_surface, StreamInteropKind.Syphon, _width, _height, _timeNs)
                with { PixelFormat = VideoPixelFormat.Bgra };
            return true;
        }
    }

    private void Run()
    {
        SyphonServerDirectory? directory = null;
        SyphonClient? client = null;
        IOSurface.IOSurface? current = null;
        IOSurface.IOSurface? previous = null;
        try
        {
            directory = new SyphonServerDirectory();

            long deadline = Stopwatch.GetTimestamp() + (8 * Stopwatch.Frequency);
            while (!_disposed && client is null && Stopwatch.GetTimestamp() < deadline)
            {
                directory.PumpEvents(TimeSpan.FromMilliseconds(50));
                int index = SelectServer(directory.GetServers(), _serverName);
                if (index >= 0)
                {
                    client = directory.CreateClient(index);
                }
            }

            if (client is null)
            {
                _connectError = new InvalidOperationException(_serverName is null
                    ? "No Syphon server found. Start a Syphon source (e.g. VTube Studio, OBS) and retry."
                    : $"No Syphon server matching '{_serverName}' was found.");
                _ready.Set();
                return;
            }

            _ready.Set();

            // Pump the run loop and poll for frames on this same thread (frame-ready notifications are
            // delivered to the thread that created the directory/client).
            while (!_disposed)
            {
                directory.PumpEvents(TimeSpan.FromMilliseconds(8));
                if (client.TryGetFrame() is not { } surface)
                {
                    continue;
                }

                // Keep the just-received surface (and the one before it) alive so it stays valid while the
                // encoder consumes the handle; release (CFRelease via dispose) anything older.
                previous?.Dispose();
                previous = current;
                current = surface;

                lock (_gate)
                {
                    _surface = surface.Handle.Handle;
                    _width = (int)surface.Width;
                    _height = (int)surface.Height;
                    _timeNs = NowNs();
                    _hasNew = true;
                }
            }
        }
        catch (Exception ex)
        {
            _connectError ??= ex;
            _ready.Set();
        }
        finally
        {
            current?.Dispose();
            previous?.Dispose();
            client?.Dispose();
            directory?.Dispose();
        }
    }

    private static int SelectServer(IReadOnlyList<SyphonServerDescription> servers, string? name)
    {
        if (servers.Count == 0)
        {
            return -1;
        }

        if (name is null)
        {
            return 0;
        }

        for (int i = 0; i < servers.Count; i++)
        {
            SyphonServerDescription server = servers[i];
            if (server.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                || server.AppName.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static long NowNs() => Stopwatch.GetTimestamp() * (1_000_000_000L / Stopwatch.Frequency);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_thread.IsAlive)
        {
            _thread.Join(TimeSpan.FromSeconds(1));
        }

        (_alpha as IDisposable)?.Dispose();
        _ready.Dispose();
    }
}

/// <summary>
/// Publishes decoded frames to a Syphon server (macOS). The VideoToolbox decoder yields <b>NV12</b>
/// IOSurfaces, so a decoded surface is GPU-converted to BGRA before publish (opaque: NV12->BGRA; alpha:
/// the side-by-side unpack), both via Metal with no CPU readback. A CPU BGRA frame is published via a
/// server-owned surface as a fallback. macOS-only.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class SyphonVideoPublishSink : IVideoFrameSink, IDisposable
{
    private readonly SyphonServer _server;
    private volatile bool _preserveAlpha;
    private IAlphaUnpacker? _alphaCodec;
    private INv12ToBgra? _nv12Converter;
    private Action<VideoFrame>? _verifyTap;
    private long _published;
    private long _firstPublishTicks;
    private bool _disposed;

    public SyphonVideoPublishSink(string serverName, bool alpha = false)
    {
        _server = new SyphonServer(serverName);
        _preserveAlpha = alpha;
    }

    private static IOSurface.IOSurface Wrap(nint handle) =>
        Runtime.GetINativeObject<IOSurface.IOSurface>(handle, owns: false)!;

    /// <summary>
    /// Adopt the publisher's negotiated side-by-side-alpha setting. Safe to call before the first frame
    /// (the Metal unpack codec is created lazily on first use), so the receiver needs no flag.
    /// </summary>
    public void SetPreserveAlpha(bool value) => _preserveAlpha = value;

    /// <summary>Enable content verification of the published GPU frames (CPU readback of the BGRA surface -&gt; <paramref name="tap"/>).</summary>
    public void EnableVerification(Action<VideoFrame> tap) => _verifyTap = tap;

    public void Submit(VideoFrame frame)
    {
        if (_disposed)
        {
            return;
        }

        if (frame.InteropKind == StreamInteropKind.Syphon && frame.Surface != 0)
        {
            // Zero-copy GPU conversion before publish (the decoder yields NV12): for alpha, unpack the
            // packed 2W x H surface back to W x H BGRA; opaque NV12 is colour-converted to BGRA; an
            // already-BGRA surface is republished directly. Codecs are created on demand for the
            // negotiated mode (set before the first frame).
            IOSurface.IOSurface decoded = Wrap(frame.Surface);
            IOSurface.IOSurface surface;
            if (_preserveAlpha)
            {
                _alphaCodec ??= new MetalAlphaCodec();
                surface = Wrap(_alphaCodec.UnpackAlpha(frame, frame.PresentationTimeNs).Surface);
            }
            else if (decoded.IsBgra())
            {
                // The VTDecompressionSession decoder yields BGRA directly (Syphon's native format), so an
                // opaque frame is announced as-is with no Metal convert.
                surface = decoded;
            }
            else
            {
                _nv12Converter ??= new MetalNv12ToBgraConverter();
                surface = Wrap(_nv12Converter.Nv12ToBgra(frame, frame.PresentationTimeNs).Surface);
            }

            _server.Publish(surface);
            if (_firstPublishTicks == 0) { _firstPublishTicks = Stopwatch.GetTimestamp(); }
            _published++;

            // GPU verify: read the published BGRA surface back to CPU and feed the verifying sink, so the real
            // GPU output is content-checked. Stamp the playout time so the readback cost doesn't inflate the
            // measured skew (markers ride luma, so the BGRA readback reports content/alpha; A/V sync is the CPU
            // path's job).
            if (_verifyTap is not null)
            {
                (int w, int h) = surface.PixelSize();
                byte[] buffer = new byte[w * h * 4];
                surface.CopyTightlyPacked(buffer);
                _verifyTap(VideoFrame.FromPixels(buffer, VideoPixelFormat.Bgra, w, h, frame.PresentationTimeNs));
            }

            // A console host has no Cocoa run loop; drain pending events (non-blocking) so the server stays
            // discoverable without stalling the publish loop.
            SyphonServer.PumpOnce();
            return;
        }
        else if (frame.InteropKind == StreamInteropKind.None
            && frame.PixelFormat == VideoPixelFormat.Bgra
            && !frame.Pixels.IsEmpty)
        {
            // CPU fallback: copy decoded BGRA pixels into a server-owned surface and publish.
            _server.PublishPixels(frame.Pixels.Span, frame.Width, frame.Height, CVPixelFormatType.CV32BGRA);
        }
        else
        {
            return;
        }

        // A console host has no Cocoa run loop; drain pending events so the server stays discoverable.
        // Non-blocking (PumpOnce, not a timed PumpEvents) so it never stalls the publish loop when idle.
        SyphonServer.PumpOnce();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_published > 0 && _firstPublishTicks != 0)
        {
            double seconds = (Stopwatch.GetTimestamp() - _firstPublishTicks) / (double)Stopwatch.Frequency;
            double fps = seconds > 0 ? _published / seconds : 0;
            Console.WriteLine($"syphon publish: {_published} frames in {seconds:F1}s ({fps:F1} fps).");
        }

        (_alphaCodec as IDisposable)?.Dispose();
        (_nv12Converter as IDisposable)?.Dispose();
        _server.Dispose();
    }
}
#endif
