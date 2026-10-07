using System.Collections.Immutable;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport;

/// <summary>
/// A use-case preset for <see cref="MediaSessionOptions"/>. The workloads have opposing needs, so the
/// profile is the one knob a host sets; it expands into codec preference, rate control tuning, loss
/// repair and playout.
/// </summary>
public enum MediaProfile
{
    /// <summary>
    /// Two-way sharing over a LAN or a good link: latency first, low loss. Broadly decodable codecs first,
    /// playout on arrival, retransmission for repair.
    /// </summary>
    InteractiveP2P,

    /// <summary>Desktop or screen with audio: sharp text and edges on a stable link.</summary>
    ScreenShare,

    /// <summary>
    /// One-way field contribution over cellular: lossy, variable and high-RTT. Forward error correction,
    /// a deep adaptive playout buffer, and lip-synced playout.
    /// </summary>
    IrlContribution,

    /// <summary>
    /// An avatar with transparency: alpha in the codec's own alpha layer where both peers code it, and
    /// otherwise colour and alpha packed side by side through an opaque codec and recomposited on the
    /// receiver.
    /// </summary>
    AvatarTransparent,
}

/// <summary>How audio and video leave the receiver.</summary>
public enum PlayoutMode
{
    /// <summary>Each frame goes to its sink as soon as it is decoded: the lowest latency, no lip sync.</summary>
    OnArrival,

    /// <summary>
    /// Audio and video are held in one adaptive buffer and released by capture time, so they lip-sync.
    /// </summary>
    Synced,
}

/// <summary>How a media session is set up.</summary>
public sealed record MediaSessionOptions
{
    /// <summary>The preset these options came from.</summary>
    public MediaProfile Profile { get; init; } = MediaProfile.InteractiveP2P;

    /// <summary>The video codecs to offer, most preferred first; each must have an encoder or decoder registered.</summary>
    public ImmutableArray<VideoCodecId> VideoCodecs { get; init; } =
    [VideoCodecId.H264, VideoCodecId.AV1, VideoCodecId.H265];

    /// <summary>The audio codecs to offer, most preferred first.</summary>
    public ImmutableArray<AudioCodecId> AudioCodecs { get; init; } = [AudioCodecId.Opus];

    /// <summary>
    /// The transport's settings: the connectivity servers, and a transport's own record for what only it
    /// knows (the WebRTC transport's <c>WebRtcTransportOptions</c>).
    /// </summary>
    public MediaTransportOptions Transport { get; init; } = new();

    /// <summary>What the video encoder's rate control is tuned for.</summary>
    public EncodeTuning VideoTuning { get; init; } = EncodeTuning.Interactive;

    /// <summary>
    /// The most video frames sent per second; a faster source, such as a capture of a 144 Hz screen, is
    /// thinned to it. Below it the source's own rate is sent, which rate control measures and plans for.
    /// Null sends every frame the source makes.
    /// </summary>
    public double? MaxFrameRate { get; init; } = 60;

    /// <summary>
    /// The longest gap between keyframes; zero for keyframes only when a receiver asks. Receivers ask
    /// after loss, so a long interval costs nothing in recovery.
    /// </summary>
    public TimeSpan KeyframeInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How a sender's transparency travels: <see cref="AlphaLayout.Layer"/> prefers the codec's own alpha
    /// layer where both peers code it (H.265 from VideoToolbox, decoded by FFmpeg) and falls back to side
    /// by side; <see cref="AlphaLayout.PackSideBySide"/> always packs. The receiver says what it takes.
    /// </summary>
    public AlphaLayout Alpha { get; init; } = AlphaLayout.None;

    /// <summary>The audio encoder's target bit rate.</summary>
    public int AudioBitsPerSecond { get; init; } = 64_000;

    /// <summary>What the audio is.</summary>
    public AudioContent AudioContent { get; init; } = AudioContent.Music;

    /// <summary>The packet loss audio plans redundancy for, in percent.</summary>
    public int AudioExpectedLossPercent { get; init; } = 10;

    /// <summary>
    /// How the receiver releases frames: synced by capture time by default, so audio and video lip-sync
    /// whatever their paths' latencies, as libwebrtc always syncs them; <see cref="PlayoutMode.OnArrival"/>
    /// for a monitor where the lowest latency matters more than sync.
    /// </summary>
    public PlayoutMode Playout { get; init; } = PlayoutMode.Synced;

    /// <summary>The shortest playout buffer in synced playout.</summary>
    public TimeSpan MinPlayoutDelay { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary>The longest playout buffer in synced playout.</summary>
    public TimeSpan MaxPlayoutDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Headroom above the measured jitter in synced playout.</summary>
    public TimeSpan PlayoutMargin { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>The options a profile starts from; adjust them with a <c>with</c> expression.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>The options.</returns>
    public static MediaSessionOptions For(MediaProfile profile) =>
        profile switch
        {
            MediaProfile.InteractiveP2P => new MediaSessionOptions(),
            MediaProfile.ScreenShare => new MediaSessionOptions
            {
                Profile = profile,
                VideoCodecs = [VideoCodecId.AV1, VideoCodecId.H265, VideoCodecId.H264],
                VideoTuning = EncodeTuning.ScreenContent,
            },
            MediaProfile.IrlContribution => new MediaSessionOptions
            {
                Profile = profile,
                VideoCodecs = [VideoCodecId.H265, VideoCodecId.AV1, VideoCodecId.H264],
                VideoTuning = EncodeTuning.LossResilient,
                AudioExpectedLossPercent = 20,
                // Forward error correction repairs loss without a retransmission round trip, which a
                // high-RTT cellular uplink cannot afford; interactive profiles repair with RTX in time.
                Transport = new MediaTransportOptions { ForwardErrorCorrection = true },
                Playout = PlayoutMode.Synced,
                MaxPlayoutDelay = TimeSpan.FromMilliseconds(800),
                PlayoutMargin = TimeSpan.FromMilliseconds(40),
            },
            MediaProfile.AvatarTransparent => new MediaSessionOptions
            {
                Profile = profile,
                Alpha = AlphaLayout.Layer,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
        };
}
