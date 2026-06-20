
using FFmpeg.AutoGen;

namespace Agash.StreamTransport.Codecs;

/// <summary>Selects (and enumerates) the usable hardware HEVC encoders for the current machine.</summary>
internal static class HevcEncoderSelector
{
    // Priority order. Each encoder is generally present only in its platform's FFmpeg build, but the desktop
    // Linux/Windows BtbN builds carry several (nvenc/amf/qsv/vaapi) regardless of which GPU is actually
    // present - so selection also probes that the encoder's hardware can be initialised (see IsUsable),
    // otherwise an AMD or Intel Linux box would pick hevc_nvenc (in the build) and fail to open it. The native
    // vendor encoders come first (NVENC for NVIDIA, QSV for Intel) so VAAPI never preempts a better same-vendor
    // path - a VAAPI driver can initialise on an NVIDIA box too, but NVENC is the right choice there. VAAPI sits
    // after them as the broad Mesa fallback (AMD, and Intel where QSV is absent); AMF (proprietary runtime) last.
    // The device type drives the cheap hardware probe; NONE means "no cheap probe, trust the platform build"
    // (videotoolbox/rkmpp/amf ship only in their own platform's FFmpeg).
    public static readonly IReadOnlyList<(string Name, AVHWDeviceType Type)> Candidates =
    [
        ("hevc_videotoolbox", AVHWDeviceType.AV_HWDEVICE_TYPE_NONE),
        ("hevc_rkmpp", AVHWDeviceType.AV_HWDEVICE_TYPE_NONE),
        ("hevc_nvenc", AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA),
        ("hevc_qsv", AVHWDeviceType.AV_HWDEVICE_TYPE_QSV),
        ("hevc_vaapi", AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI),
        ("hevc_amf", AVHWDeviceType.AV_HWDEVICE_TYPE_NONE),
    ];

    public static string Select(string? preferred = null)
    {
        if (preferred is not null)
        {
            return FFmpegLibrary.HasEncoder(preferred)
                ? preferred
                : throw new NotSupportedException($"Requested HEVC encoder '{preferred}' is not present in the FFmpeg build.");
        }

        IReadOnlyList<string> usable = UsableEncoders();
        return usable.Count > 0
            ? usable[0]
            : throw new NotSupportedException(
                "No usable hardware HEVC encoder (vaapi/nvenc/amf/qsv/rkmpp/videotoolbox) is available on this machine.");
    }

    /// <summary>The usable hardware encoders, in priority order - present in the build and with usable hardware.</summary>
    public static IReadOnlyList<string> UsableEncoders()
    {
        var usable = new List<string>();
        foreach ((string name, AVHWDeviceType type) in Candidates)
        {
            if (FFmpegLibrary.HasEncoder(name) && IsUsable(name, type))
            {
                usable.Add(name);
            }
        }

        return usable;
    }

    /// <summary>Whether the hardware backing an encoder can actually be initialised on this host.</summary>
    public static bool IsUsable(string name, AVHWDeviceType type) => name switch
    {
        // VAAPI goes through the shared process-wide device (see VaapiDevice), not a throwaway probe device.
        "hevc_vaapi" => VaapiDevice.IsAvailable(),
        _ => CodecProbe.HwDeviceUsable(type),
    };
}

/// <summary>
/// Encodes outgoing video to HEVC. The hardware encoder is created lazily on the first frame, once the
/// dimensions are known. Currently the CUDA/NVENC backend accepts CPU NV12 (or I420) frames; the GPU
/// texture (zero-copy) input is layered on in a later step.
/// </summary>
internal sealed class VideoSendPipeline(int fps, long bitrate, string? encoderName = null, nint gpuDeviceHandle = 0, bool preserveAlpha = false, int maxBFrames = 0) : IVideoEncoder
{
    private const int ClockRate = 90_000;
    private readonly string _encoderName = HevcEncoderSelector.Select(encoderName);
    private readonly CpuSurfaceTransform _cpuTransform = new();
    private IVideoEncoderBackend? _backend;
    private long _lastCaptureNs = -1;


    /// <summary>The shared GPU device handle the zero-copy encoder runs on (Windows), or 0.</summary>
    public nint GpuDeviceHandle => gpuDeviceHandle;

    /// <summary>
    /// The native GPU device handle (<c>ID3D11Device*</c> on Windows) a capture source opens its shared
    /// surface onto, once the first GPU-surface frame has created the zero-copy encoder. Zero otherwise.
    /// </summary>
    public nint NativeDevice => _backend?.NativeDevice ?? 0;

    public EncodedVideoAccessUnit? Encode(VideoFrame frame)
    {
        // CPU sources are normalised to NV12 (and side-by-side-packed when alpha) before encode; GPU-surface
        // frames already carry their native surface (pre-packed by the capture source when alpha). The
        // keyframe-on-demand request rides through the normalisation (a fresh NV12 frame is built).
        VideoFrame prepared = frame.InteropKind == StreamInteropKind.None
            ? PrepareCpuFrame(frame) with { ForceKeyframe = frame.ForceKeyframe }
            : frame;

        // Create the codec backend lazily on the first frame, when dimensions are known: the encoder class
        // *is* the backend (no wrapper), constructed here at the prepared frame's size.
        _backend ??= CreateBackend(prepared);

        // The encoder threads the frame's capture timestamp through FFmpeg as the packet PTS and hands back the
        // capture time of the frame that actually produced this access unit (it lags - and, with B-frames,
        // reorders - relative to the submitted frame). Allocation-free: no FIFO or map, just the recovered PTS.
        // Use that producing capture time for both the RTP duration and the caller's abs-capture-time.
        byte[]? accessUnit = _backend.Encode(prepared, out long captureNs);
        return accessUnit is null ? null : new EncodedVideoAccessUnit(DurationFromCaptureTime(captureNs), accessUnit, captureNs);
    }

    // The RTP timestamp must advance by the REAL capture interval, not a fixed ClockRate/fps - otherwise a
    // source running off its nominal rate (a synthetic 30 fps that actually delivers 32 fps, or a jittery
    // camera) makes the video RTP clock drift against the audio clock, which breaks receiver A/V sync. Derive
    // the per-frame duration from the frame's capture timestamp delta; the first frame (and any non-monotonic
    // timestamp) falls back to the nominal interval.
    private uint DurationFromCaptureTime(long captureNs)
    {
        if (_lastCaptureNs < 0 || captureNs <= _lastCaptureNs)
        {
            _lastCaptureNs = captureNs;
            return (uint)(ClockRate / fps);
        }

        long deltaNs = captureNs - _lastCaptureNs;
        _lastCaptureNs = captureNs;
        return (uint)(deltaNs * ClockRate / 1_000_000_000L);
    }

    private IVideoEncoderBackend CreateBackend(in VideoFrame frame) => frame.InteropKind switch
    {
#if WINDOWS_HEAD
        // The Spout texture's native format (BGRA) goes straight to the ASIC; gpuDeviceHandle is the shared device.
        StreamInteropKind.Spout => new D3D11VideoEncoder(_encoderName, frame.Width, frame.Height, fps, bitrate, frame.PixelFormat, gpuDeviceHandle),
#endif
        StreamInteropKind.Syphon => new VideoToolboxVideoEncoder(frame.Width, frame.Height, fps, bitrate),
        StreamInteropKind.None => CreateCpuEncoderWithFallback(frame),
        _ => throw new NotSupportedException($"Interop kind {frame.InteropKind} is not supported on this platform."),
    };

    // CPU-input (None) path. A pinned encoder is honoured exactly (no silent substitution). Otherwise try the
    // usable hardware encoders in priority order: device-probe says the GPU is present, but an encoder can still
    // fail to *open* (NVENC session caps, an unsupported resolution/profile, a half-initialised driver), so fall
    // back to the next usable candidate instead of failing the whole send.
    private IVideoEncoderBackend CreateCpuEncoderWithFallback(in VideoFrame frame)
    {
        if (encoderName is not null)
        {
            return _encoderName == "hevc_vaapi"
                ? new VaapiVideoEncoder(frame.Width, frame.Height, fps, bitrate)
                : new HardwareHevcEncoder(_encoderName, frame.Width, frame.Height, fps, bitrate, maxBFrames);
        }

        Exception? last = null;
        foreach (string name in HevcEncoderSelector.UsableEncoders())
        {
            try
            {
                return name == "hevc_vaapi"
                    ? new VaapiVideoEncoder(frame.Width, frame.Height, fps, bitrate)
                    : new HardwareHevcEncoder(name, frame.Width, frame.Height, fps, bitrate, maxBFrames);
            }
            catch (Exception ex) when (ex is HardwareEncoderUnavailableException or NotSupportedException)
            {
                last = ex; // probed-usable but failed to open; try the next candidate
            }
        }

        throw new NotSupportedException("No hardware HEVC encoder could be opened on this machine.", last);
    }

    // The CPU surface transform (last resort, CPU-memory sources only): a BGRA frame with alpha is packed
    // side-by-side (colour | alpha-as-luma) into a 2W x H NV12 frame so an ordinary opaque codec carries
    // transparency; opaque sources are normalised to NV12. GPU-surface sources pack on the GPU upstream and
    // never reach here. The encoder backend then runs at the prepared frame's width.
    private VideoFrame PrepareCpuFrame(in VideoFrame frame) =>
        preserveAlpha && frame.PixelFormat == VideoPixelFormat.Bgra
            ? _cpuTransform.PackAlpha(frame, frame.PresentationTimeNs)
            : _cpuTransform.ToEncoderNv12(frame, frame.PresentationTimeNs);

    /// <summary>Retune the live encoder to a new congestion-driven target bitrate (no-op until the backend exists).</summary>
    public void UpdateBitrate(long bitrateBps) => _backend?.UpdateBitrate(bitrateBps);

    public void Dispose() => _backend?.Dispose();
}

/// <summary>
/// Decodes incoming HEVC access units to <see cref="VideoFrame"/>s for the sink. When GPU output is
/// preferred (Windows only) it decodes straight into D3D11 GPU textures for a zero-copy publish;
/// otherwise it decodes to CPU I420/NV12 frames.
/// </summary>
internal sealed class VideoReceivePipeline : IVideoDecoder
{
    private readonly IVideoDecoderBackend _decoder;
    private readonly CpuSurfaceTransform _cpuTransform = new();
    private volatile bool _preserveAlpha;

    public VideoReceivePipeline(bool preferGpuOutput = false, bool preserveAlpha = false)
    {
        _preserveAlpha = preserveAlpha;
        _decoder = VideoDecoderBackendFactory.Create(preferGpuOutput);
    }

    /// <summary>True when frames are decoded into GPU surfaces (zero-copy), false for the CPU path.</summary>
    public bool IsGpuOutput => _decoder.OutputSurfaceKind != StreamInteropKind.None;


    /// <summary>The native device handle the GPU output surface lives on (ID3D11Device* on Windows), or 0.</summary>
    public nint NativeDevice => _decoder.NativeDevice;

    /// <summary>
    /// Set whether the decoded stream carries side-by-side alpha, before the first frame is decoded. Used by
    /// the receiver to adopt the value the publisher negotiated over the signaling channel, so the subscriber
    /// needs no alpha flag of its own.
    /// </summary>
    public void SetPreserveAlpha(bool value) => _preserveAlpha = value;

    public VideoFrame? Decode(ReadOnlySpan<byte> accessUnit, uint rtpTimestamp, long presentationTimeNs, out uint frameRtpTimestamp)
    {
        if (!_decoder.TryDecode(accessUnit, rtpTimestamp, presentationTimeNs, out VideoFrame frame, out frameRtpTimestamp))
        {
            return null;
        }

        // CPU path + alpha: split the packed 2W x H colour|alpha frame back to W x H BGRA (last-resort CPU
        // transform). GPU-surface backends hand the packed surface to the publish sink, which unpacks on the GPU.
        if (_decoder.OutputSurfaceKind == StreamInteropKind.None && _preserveAlpha)
        {
            return _cpuTransform.UnpackAlpha(frame, presentationTimeNs);
        }

        return frame;
    }

    public void Dispose() => _decoder.Dispose();
}
