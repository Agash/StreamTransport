
namespace Agash.StreamTransport.Codecs;

/// <summary>Picks the first available hardware HEVC encoder for the current machine.</summary>
internal static class HevcEncoderSelector
{
    // Priority, naturally routed by platform since each encoder is present only in its platform's build:
    // Apple VideoToolbox (macOS), then Rockchip rkmpp (linux-arm64 IRL boards), then on Windows the
    // discrete/integrated GPUs in order NVIDIA, Intel QSV, AMD.
    private static readonly string[] s_candidates =
        ["hevc_videotoolbox", "hevc_rkmpp", "hevc_nvenc", "hevc_qsv", "hevc_amf"];

    public static string Select(string? preferred = null)
    {
        if (preferred is not null)
        {
            return FFmpegLibrary.HasEncoder(preferred)
                ? preferred
                : throw new NotSupportedException($"Requested HEVC encoder '{preferred}' is not present in the FFmpeg build.");
        }

        foreach (string name in s_candidates)
        {
            if (FFmpegLibrary.HasEncoder(name))
            {
                return name;
            }
        }

        throw new NotSupportedException(
            "No hardware HEVC encoder (nvenc/amf/qsv/mf) is available in the FFmpeg build on this machine.");
    }
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
        StreamInteropKind.None => _encoderName == "hevc_vaapi"
            ? new VaapiVideoEncoder(frame.Width, frame.Height, fps, bitrate)
            : new HardwareHevcEncoder(_encoderName, frame.Width, frame.Height, fps, bitrate, maxBFrames),
        _ => throw new NotSupportedException($"Interop kind {frame.InteropKind} is not supported on this platform."),
    };

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
