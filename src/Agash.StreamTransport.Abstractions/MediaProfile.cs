namespace Agash.StreamTransport;

/// <summary>
/// A use-case preset that selects sensible <see cref="MediaTransportOptions"/> defaults. The three real
/// workloads have opposing needs, so the profile is the single knob a host sets; it expands
/// into codec preference, B-frame budget, and playout posture. Repair selection (FEC/RED) and the mobility
/// layer are profile-gated too, and attach as those capabilities land.
/// </summary>
public enum MediaProfile
{
    /// <summary>
    /// Two-way avatar sharing over a LAN / good link: latency-paramount, low loss. No B-frames, low-latency
    /// present-on-arrival playout, broadly-decodable codecs first. RTX+NACK repairs within the playout budget,
    /// so FEC is intentionally off.
    /// </summary>
    InteractiveP2P,

    /// <summary>
    /// Desktop/screen + audio: content-sensitive (text/edges), stable link, latency-tolerant-ish. Prefers
    /// efficient/screen-friendly codecs; cheap audio redundancy over video FEC.
    /// </summary>
    ScreenShare,

    /// <summary>
    /// One-way IRL field contribution over cellular: lossy, variable, mobile, high RTT. Trades a deeper
    /// adaptive jitter buffer and B-frames for bandwidth; this is the profile that engages aggressive
    /// congestion adaptation, FEC, and the mobility layer.
    /// </summary>
    IrlContribution,
}

/// <summary>Builds the <see cref="MediaTransportOptions"/> baseline for a <see cref="MediaProfile"/>.</summary>
public static class MediaProfiles
{
    /// <summary>
    /// The options preset for <paramref name="profile"/>. Callers can further customise the result with a
    /// <c>with</c> expression (e.g. to add STUN/ICE servers).
    /// </summary>
    public static MediaTransportOptions Create(MediaProfile profile) => profile switch
    {
        MediaProfile.InteractiveP2P => new MediaTransportOptions
        {
            Profile = MediaProfile.InteractiveP2P,
            VideoCodecs = [VideoCodec.H264, VideoCodec.Av1, VideoCodec.H265],
            MaxVideoBFrames = 0,
            PlayoutMode = PlayoutMode.LowLatencyMonitor,
        },
        MediaProfile.ScreenShare => new MediaTransportOptions
        {
            Profile = MediaProfile.ScreenShare,
            VideoCodecs = [VideoCodec.Av1, VideoCodec.H265, VideoCodec.H264],
            MaxVideoBFrames = 0,
            PlayoutMode = PlayoutMode.LowLatencyMonitor,
        },
        MediaProfile.IrlContribution => new MediaTransportOptions
        {
            Profile = MediaProfile.IrlContribution,
            VideoCodecs = [VideoCodec.H265, VideoCodec.Av1],
            // No B-frames. They save uplink bandwidth, but each adds a reference dependency, so over a lossy
            // one-way link a single dropped reference-frame packet stalls every frame that depends on it -
            // FlexFEC recovers a single lost packet, not a whole lost frame. On a lossy link B-frames collapse
            // the decoded frame rate under cascading reference-picture-set errors; without them it holds up.
            MaxVideoBFrames = 0,
            // FlexFEC masks loss without a retransmit round trip - the right repair for a high-RTT cellular
            // uplink. On the low-RTT interactive/screen-share profiles it is off (RTX repairs in time).
            EnableFec = true,
            PlayoutMode = PlayoutMode.Synced,
            MaxPlayoutDelay = TimeSpan.FromMilliseconds(800),
            PlayoutMargin = TimeSpan.FromMilliseconds(40),
        },
        _ => new MediaTransportOptions(),
    };
}
