namespace Agash.StreamTransport;

/// <summary>
/// Video codecs the transport can negotiate. SDP negotiation picks the most efficient codec both
/// peers support.
/// </summary>
public enum VideoCodec
{
    /// <summary>H.264 / AVC. Widest compatibility.</summary>
    H264,

    /// <summary>H.265 / HEVC. Hardware-accelerated on all target platforms; the default.</summary>
    H265,

    /// <summary>AV1. Most efficient and supports alpha, but hardware encode is less common.</summary>
    Av1,
}

/// <summary>Audio codecs the transport can negotiate.</summary>
public enum AudioCodec
{
    /// <summary>Opus, the standard WebRTC audio codec. Pure-managed encode/decode; no native dependency.</summary>
    Opus,
}

/// <summary>Interleaved PCM sample format for an <see cref="AudioFrame"/>.</summary>
public enum AudioSampleFormat
{
    /// <summary>16-bit signed integer.</summary>
    S16,

    /// <summary>32-bit IEEE float.</summary>
    F32,
}

/// <summary>The platform GPU texture-sharing mechanism a frame is captured from or published to.</summary>
public enum StreamInteropKind
{
    /// <summary>No platform interop (raw frames).</summary>
    None,

    /// <summary>Spout (Windows, DirectX 11 shared texture).</summary>
    Spout,

    /// <summary>Syphon (macOS, IOSurface).</summary>
    Syphon,

    /// <summary>PipeWire (Linux, DMA-BUF).</summary>
    PipeWire,
}

/// <summary>Pixel layout of a CPU <see cref="VideoFrame"/>.</summary>
public enum VideoPixelFormat
{
    /// <summary>Planar Y plane followed by interleaved UV, 4:2:0 (the codec-native upload format).</summary>
    Nv12,

    /// <summary>Planar Y, U, V, 4:2:0.</summary>
    I420,

    /// <summary>Packed 8-bit B, G, R, A.</summary>
    Bgra,
}

/// <summary>
/// A video frame, either GPU-resident (a platform surface handle, for zero-copy hardware encode) or a
/// CPU pixel buffer. When <see cref="InteropKind"/> is not <see cref="StreamInteropKind.None"/>,
/// <see cref="Surface"/> is the GPU handle - a D3D11 texture (Spout), an IOSurface (Syphon), or a
/// DMA-BUF (PipeWire). When it is <see cref="StreamInteropKind.None"/>, <see cref="Pixels"/> holds the
/// CPU samples in <see cref="PixelFormat"/>.
/// </summary>
public readonly record struct VideoFrame
{
    /// <summary>Create a GPU-resident frame backed by a platform surface handle.</summary>
    public static VideoFrame FromSurface(nint surface, StreamInteropKind interopKind, int width, int height, long presentationTimeNs) =>
        new()
        {
            Surface = surface,
            InteropKind = interopKind,
            Width = width,
            Height = height,
            PresentationTimeNs = presentationTimeNs,
        };

    /// <summary>Create a CPU frame backed by a pixel buffer.</summary>
    public static VideoFrame FromPixels(ReadOnlyMemory<byte> pixels, VideoPixelFormat pixelFormat, int width, int height, long presentationTimeNs) =>
        new()
        {
            Pixels = pixels,
            PixelFormat = pixelFormat,
            Width = width,
            Height = height,
            PresentationTimeNs = presentationTimeNs,
        };

    /// <summary>
    /// Create a GPU-resident frame backed by a Linux DMA-BUF surface (<see cref="StreamInteropKind.PipeWire"/>).
    /// The pixels stay on the GPU; the encoder imports the planes as a VAAPI surface (DRM-PRIME) and the
    /// publish sink can hand them straight back to PipeWire - no CPU readback.
    /// </summary>
    public static VideoFrame FromDmaBuf(in DmaBufSurface surface, int width, int height, long presentationTimeNs) =>
        new()
        {
            DmaBuf = surface,
            InteropKind = StreamInteropKind.PipeWire,
            Width = width,
            Height = height,
            PresentationTimeNs = presentationTimeNs,
        };

    /// <summary>
    /// The GPU surface handle for a single-handle interop (Spout D3D11 texture, Syphon IOSurface), valid
    /// when <see cref="InteropKind"/> is <see cref="StreamInteropKind.Spout"/> or
    /// <see cref="StreamInteropKind.Syphon"/>. For <see cref="StreamInteropKind.PipeWire"/> use <see cref="DmaBuf"/>.
    /// </summary>
    public nint Surface { get; init; }

    /// <summary>
    /// The DMA-BUF surface (per-plane fds + modifier) for a <see cref="StreamInteropKind.PipeWire"/> frame;
    /// <see langword="null"/> otherwise. A value type, so this allocates nothing per frame.
    /// </summary>
    public DmaBufSurface? DmaBuf { get; init; }

    /// <summary>The platform interop kind, or <see cref="StreamInteropKind.None"/> for a CPU frame.</summary>
    public StreamInteropKind InteropKind { get; init; }

    /// <summary>The CPU pixel buffer, valid when <see cref="InteropKind"/> is <see cref="StreamInteropKind.None"/>.</summary>
    public ReadOnlyMemory<byte> Pixels { get; init; }

    /// <summary>The layout of <see cref="Pixels"/>.</summary>
    public VideoPixelFormat PixelFormat { get; init; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; init; }

    /// <summary>Capture time in nanoseconds from a monotonic clock shared with audio, used for A/V sync.</summary>
    public long PresentationTimeNs { get; init; }

    /// <summary>
    /// Request that the encoder emit this frame as a keyframe (IDR) regardless of its GOP cadence. Used for
    /// keyframe-on-demand - e.g. a late-joining subscriber, recovery after loss, or a self-describing test
    /// marker that must decode cleanly on its own. Honoured by the hardware HEVC encoder; ignored where the
    /// backend cannot force a frame type.
    /// </summary>
    public bool ForceKeyframe { get; init; }
}

/// <summary>A buffer of interleaved PCM audio samples with a capture timestamp.</summary>
/// <param name="Samples">Interleaved PCM samples.</param>
/// <param name="Format">The sample format.</param>
/// <param name="SampleRate">Samples per second.</param>
/// <param name="Channels">Channel count.</param>
/// <param name="PresentationTimeNs">
/// Capture time in nanoseconds from the same monotonic clock as video, used for A/V sync.
/// </param>
public readonly record struct AudioFrame(
    ReadOnlyMemory<byte> Samples, AudioSampleFormat Format, int SampleRate, int Channels, long PresentationTimeNs);

/// <summary>Produces video frames for the sender to encode (adapted over a platform capture source).</summary>
public interface IVideoFrameSource
{
    /// <summary>Try to obtain the latest video frame. Returns false when none is available.</summary>
    bool TryGetFrame(out VideoFrame frame);
}

/// <summary>Receives decoded video frames from the receiver (adapted over a platform publish sink).</summary>
public interface IVideoFrameSink
{
    /// <summary>Submit a decoded video frame for output.</summary>
    void Submit(VideoFrame frame);
}

/// <summary>Produces PCM audio frames for the sender to encode (adapted over a platform audio capture).</summary>
public interface IAudioFrameSource
{
    /// <summary>Try to obtain the next audio frame. Returns false when none is available.</summary>
    bool TryGetFrame(out AudioFrame frame);
}

/// <summary>Receives decoded PCM audio frames from the receiver (adapted over a platform audio output).</summary>
public interface IAudioFrameSink
{
    /// <summary>Submit a decoded audio frame for output.</summary>
    void Submit(AudioFrame frame);
}

/// <summary>Options for a media session: the codecs to offer and the ICE/STUN configuration.</summary>
/// <remarks>
/// Audio/video synchronisation is handled by WebRTC. When both an audio and a video track are carried
/// on one peer connection and each frame is stamped (<see cref="VideoFrame.PresentationTimeNs"/> /
/// <see cref="AudioFrame.PresentationTimeNs"/>) from a common monotonic clock, the sender's RTCP sender
/// reports tie the per-track RTP timestamps to a shared NTP wall-clock and the receiver lip-syncs the
/// two streams. The transport only needs correctly-stamped frames on one connection.
/// </remarks>
public sealed record MediaTransportOptions
{
    /// <summary>Video codecs to offer, in preference order. The first mutually-supported one is used.</summary>
    public IReadOnlyList<VideoCodec> VideoCodecs { get; init; } = [VideoCodec.H265, VideoCodec.H264];

    /// <summary>
    /// Optional explicit hardware H.265 encoder name (e.g. "hevc_nvenc", "hevc_amf", "hevc_qsv"). When
    /// null the most suitable available encoder is auto-selected. Pin this to target a specific GPU
    /// vendor or to test a particular encoder end to end.
    /// </summary>
    public string? VideoEncoderName { get; init; }

    /// <summary>
    /// Nominal video frame rate, used for encoder rate-control/keyframe-interval tuning. The actual send rate
    /// follows the source's real capture cadence (RTP timestamps advance by the measured interval), so this is
    /// a hint, not a throttle. Defaults to 30.
    /// </summary>
    public int VideoFps { get; init; } = 30;

    /// <summary>The audio codec. Opus is the only WebRTC audio codec.</summary>
    public AudioCodec AudioCodec { get; init; } = AudioCodec.Opus;

    /// <summary>Public STUN server URLs for ICE (e.g. stun:stun.l.google.com:19302). No TURN.</summary>
    public IReadOnlyList<string> StunServers { get; init; } = [];

    /// <summary>
    /// Fully-specified ICE servers, including any TURN entries with credentials. These are typically
    /// provisioned by the signaling router (delivered in the peer's <see cref="WelcomeMessage"/>) and
    /// supplement <see cref="StunServers"/>. STUN entries leave the credentials null.
    /// </summary>
    public IReadOnlyList<IceServer> IceServers { get; init; } = [];

    /// <summary>
    /// Restricts ICE host-candidate gathering to local addresses matching at least one selector: a NIC name, a
    /// literal IP address, or the keyword <c>ipv4</c>/<c>ipv6</c> (case-insensitive). Empty (the default) gathers
    /// every usable address. Pin a family on both peers to force it for the link; pin NIC names to confine an IRL
    /// field uplink to its cellular modems instead of also offering Wi-Fi.
    /// </summary>
    public IReadOnlyList<string> LocalAddressPreferences { get; init; } = [];

    /// <summary>
    /// When true the receiver decodes video directly into GPU textures (Windows D3D11) for a zero-copy
    /// publish, emitting GPU-surface <see cref="VideoFrame"/>s. When false it decodes to CPU frames.
    /// </summary>
    public bool PreferGpuVideoOutput { get; init; }

    /// <summary>
    /// When true the sender preserves the source alpha channel by packing colour and alpha side by side
    /// into one <c>2W x H</c> frame (left = colour, right = alpha-as-luma) before encode, and the receiver
    /// splits it back to BGRA-with-alpha after decode. Codec-agnostic and works on every hardware encoder
    /// (the alpha rides the full-resolution luma plane). Only meaningful for sources that carry alpha
    /// (Spout/Syphon/PipeWire desktop surfaces); ignored for opaque/camera feeds. Both peers must agree -
    /// the sender advertises it and the receiver matches via the signaling channel.
    /// </summary>
    public bool PreserveAlpha { get; init; }

    /// <summary>How the receiver times presentation of decoded frames. See <see cref="PlayoutMode"/>.</summary>
    public PlayoutMode PlayoutMode { get; init; } = PlayoutMode.LowLatencyMonitor;

    /// <summary>
    /// In <see cref="PlayoutMode.Synced"/> the receiver's jitter buffer adapts its depth to measured network
    /// jitter between these bounds (plus <see cref="PlayoutMargin"/> headroom), holding the earlier-arriving
    /// stream to lip-sync with the later one. On a clean link the depth shrinks toward <see cref="MinPlayoutDelay"/>
    /// for low latency; on a jittery cellular link it grows toward <see cref="MaxPlayoutDelay"/> to avoid
    /// underruns. The floor must still cover the inter-stream pipeline gap (decode + render). Ignored in other modes.
    /// </summary>
    public TimeSpan MinPlayoutDelay { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary>The upper bound on the adaptive jitter-buffer depth, capping worst-case latency. See <see cref="MinPlayoutDelay"/>.</summary>
    public TimeSpan MaxPlayoutDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Headroom added above the measured jitter when sizing the adaptive buffer. See <see cref="MinPlayoutDelay"/>.</summary>
    public TimeSpan PlayoutMargin { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Maximum consecutive B-frames the video encoder may use. B-frames improve compression (worthwhile on a
    /// bandwidth-constrained uplink) at the cost of roughly this many frame-times of reorder latency on each
    /// side. Default 0 for the lowest-latency interactive/monitor path; raise it (e.g. 1-2) for a one-way
    /// contribution feed where a little latency buys uplink bandwidth. Sync stays correct either way - the RTP
    /// timestamp is the frame's capture instant and abs-capture-time carries the true capture clock.
    /// </summary>
    public int MaxVideoBFrames { get; init; }

    /// <summary>
    /// Enable FlexFEC (RFC 8627) loss repair for video: send a repair packet per group of media packets so the
    /// receiver recovers a single lost packet per group without a retransmit round trip. Worthwhile on a lossy,
    /// high-RTT uplink (the IRL profile enables it); wasteful on a low-RTT link where RTX repairs in time.
    /// </summary>
    public bool EnableFec { get; init; }
}

/// <summary>How the receiver schedules presentation of decoded audio/video frames.</summary>
public enum PlayoutMode
{
    /// <summary>
    /// Present every frame on arrival - lowest latency, no buffering. Correct for video-only (avatar/overlay)
    /// and audio-only streams, where there is nothing to lip-sync. The default.
    /// </summary>
    LowLatencyMonitor,

    /// <summary>
    /// When both audio and video are present, align them on the sender's capture clock (preferring the
    /// abs-capture-time header extension, falling back to RTCP Sender Reports) and present through an adaptive
    /// jitter buffer so they lip-sync. Falls back to present-on-arrival until the clocks align and for
    /// single-stream sessions, so it is safe to leave on.
    /// </summary>
    Synced,
}

/// <summary>Captures, encodes, and sends audio and/or video to a remote peer over WebRTC.</summary>
public interface IMediaSender : IAsyncDisposable
{
    /// <summary>Begin the session: negotiate via <paramref name="signaling"/> and start sending.</summary>
    Task StartAsync(ISignalingChannel signaling, CancellationToken cancellationToken = default);

    /// <summary>Stop sending and tear down the peer connection.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>Receives, decodes, and outputs audio and/or video from a remote peer.</summary>
public interface IMediaReceiver : IAsyncDisposable
{
    /// <summary>Begin the session: negotiate via <paramref name="signaling"/> and start receiving.</summary>
    Task StartAsync(ISignalingChannel signaling, CancellationToken cancellationToken = default);

    /// <summary>Stop receiving and tear down the peer connection.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Adopt the publisher's negotiated side-by-side-alpha setting (no-op for audio-only).</summary>
    void SetPreserveAlpha(bool value);

    /// <summary>
    /// Set the differential output-path latency <c>Lv - La</c> (video publish latency minus audio device latency),
    /// in nanoseconds, so the synced playout lip-syncs at the <i>output</i> boundary (display / speaker) rather
    /// than only at scheduler release. The host computes it from the latencies of the concrete sinks it owns.
    /// 0 (the default) keeps scheduler-aligned playout. No-op unless both streams are present and synced.
    /// </summary>
    void SetOutputLatencyOffset(long differentialNs);
}
