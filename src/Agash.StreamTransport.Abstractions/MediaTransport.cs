using System.Collections.Immutable;
using System.Net;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport;

/// <summary>Which side of the negotiation a session takes.</summary>
public enum MediaSessionRole
{
    /// <summary>Makes the offer, and controls connectivity checks.</summary>
    Offerer,

    /// <summary>Waits for an offer and answers it.</summary>
    Answerer,
}

/// <summary>Where a transport's connection stands.</summary>
public enum TransportState
{
    /// <summary>Not started.</summary>
    New,

    /// <summary>Negotiating and establishing the path.</summary>
    Connecting,

    /// <summary>Media can flow.</summary>
    Connected,

    /// <summary>The path was lost; the transport is trying to recover it.</summary>
    Disconnected,

    /// <summary>The connection could not be established or recovered.</summary>
    Failed,

    /// <summary>Closed.</summary>
    Closed,
}

/// <summary>The two ends of the path a session's media takes.</summary>
/// <param name="Local">This side's endpoint.</param>
/// <param name="Remote">The peer's endpoint.</param>
/// <param name="Relayed">Whether media goes through a relay rather than directly between the peers.</param>
public readonly record struct MediaRoute(EndPoint Local, EndPoint Remote, bool Relayed = false);

/// <summary>A video codec as offered: its format and the ways it can carry alpha, most preferred first.</summary>
/// <param name="Format">The codec and its parameters.</param>
/// <param name="Alpha">The alpha layouts this side can send or take with it; empty for none.</param>
public sealed record VideoCodecOffer(VideoCodecFormat Format, ImmutableArray<AlphaLayout> Alpha);

/// <summary>What a session offers for video.</summary>
/// <param name="Codecs">The codecs, most preferred first.</param>
/// <param name="Sends">Whether this side sends video.</param>
/// <param name="Receives">Whether this side receives video.</param>
public sealed record VideoOffer(ImmutableArray<VideoCodecOffer> Codecs, bool Sends, bool Receives);

/// <summary>What a session offers for audio.</summary>
/// <param name="Codecs">The codecs, most preferred first.</param>
/// <param name="Sends">Whether this side sends audio.</param>
/// <param name="Receives">Whether this side receives audio.</param>
public sealed record AudioOffer(ImmutableArray<AudioCodecFormat> Codecs, bool Sends, bool Receives);

/// <summary>What a session offers to send and receive.</summary>
/// <param name="Video">The video, or null for none.</param>
/// <param name="Audio">The audio, or null for none.</param>
public sealed record MediaOffer(VideoOffer? Video, AudioOffer? Audio);

/// <summary>What both sides agreed for video.</summary>
/// <param name="Encode">The format to encode, holding what the receiver declared it decodes.</param>
/// <param name="Decode">The format to decode.</param>
/// <param name="Alpha">How alpha travels.</param>
public sealed record NegotiatedVideo(
    VideoCodecFormat Encode,
    VideoCodecFormat Decode,
    AlphaLayout Alpha
);

/// <summary>What both sides agreed for audio.</summary>
/// <param name="Format">The codec and its parameters.</param>
public sealed record NegotiatedAudio(AudioCodecFormat Format);

/// <summary>What both sides agreed.</summary>
/// <param name="Video">The video, or null when none was agreed.</param>
/// <param name="Audio">The audio, or null when none was agreed.</param>
public sealed record NegotiatedMedia(NegotiatedVideo? Video, NegotiatedAudio? Audio);

/// <summary>Why a video frame carries timing: on a schedule, for its size, or both.</summary>
[Flags]
public enum FrameTimingReasons : byte
{
    /// <summary>Not a timing frame.</summary>
    None = 0,

    /// <summary>Chosen by the periodic timer, every 200 ms of capture time.</summary>
    Timer = 1,

    /// <summary>Chosen because it is five times the average frame or more.</summary>
    Size = 2,
}

/// <summary>
/// The sender's encode timing of one video frame, as offsets from its capture, for tracing latency frame by
/// frame (a timing frame). The transport adds when packetization finished and the last packet left.
/// </summary>
/// <param name="EncodeStart">When the encoder took the frame.</param>
/// <param name="EncodeFinish">When the encoder produced it.</param>
/// <param name="Reasons">Why the frame is timed.</param>
public readonly record struct FrameSendTiming(
    TimeSpan EncodeStart,
    TimeSpan EncodeFinish,
    FrameTimingReasons Reasons
);

/// <summary>
/// The timing a received video frame carried and how it arrived: the sender's stamps as offsets from
/// capture on its clock, and when the frame's first and last packets reached this side, on this side's
/// wall clock.
/// </summary>
/// <param name="Reasons">Why the sender timed the frame.</param>
/// <param name="EncodeStart">When the sender's encoder took the frame.</param>
/// <param name="EncodeFinish">When the sender's encoder produced it.</param>
/// <param name="PacketizationFinish">When the sender had its packets queued.</param>
/// <param name="PacerExit">When its last packet left the sender.</param>
/// <param name="FirstPacketReceived">When its first packet arrived here.</param>
/// <param name="LastPacketReceived">When its last packet arrived here.</param>
public readonly record struct VideoReceiveTiming(
    FrameTimingReasons Reasons,
    TimeSpan EncodeStart,
    TimeSpan EncodeFinish,
    TimeSpan PacketizationFinish,
    TimeSpan PacerExit,
    NtpTime FirstPacketReceived,
    NtpTime LastPacketReceived
);

/// <summary>
/// Takes the media a transport receives, on the transport's receive thread. Each frame's buffer passes to
/// the consumer, which disposes it.
/// </summary>
public interface IReceivedMediaConsumer
{
    /// <summary>A complete video access unit.</summary>
    /// <param name="frame">The access unit.</param>
    /// <param name="keyframe">Whether it can be decoded on its own.</param>
    /// <param name="capture">When the sender captured it, on the sender's wall clock, when known.</param>
    /// <param name="timing">The frame's timing, when the sender timed it.</param>
    void OnVideoFrame(
        EncodedFrameBuffer frame,
        bool keyframe,
        NtpTime? capture,
        VideoReceiveTiming? timing
    );

    /// <summary>An audio packet.</summary>
    /// <param name="packet">The packet.</param>
    /// <param name="sequence">Its number in the stream, so a gap shows how many were lost.</param>
    /// <param name="position">Where it starts on the stream's timeline.</param>
    /// <param name="capture">When the sender captured its first sample, when known.</param>
    void OnAudioPacket(
        EncodedFrameBuffer packet,
        long sequence,
        TimeSpan position,
        NtpTime? capture
    );
}

/// <summary>Counters and estimates of a transport, for telemetry.</summary>
/// <param name="Capacity">The path's capacity as the congestion controller sees it.</param>
/// <param name="LossRate">The fraction of packets lost recently, 0 to 1.</param>
/// <param name="PacketsSent">Media packets sent.</param>
/// <param name="RetransmissionsSent">Packets sent again because the receiver missed them.</param>
/// <param name="RetransmissionsRequested">Packets this side asked the sender for again.</param>
/// <param name="Recovered">Lost packets recovered on receipt.</param>
/// <param name="KeyframeRequestsSent">Keyframe requests sent to the peer.</param>
/// <param name="CircuitBreaker">Whether media flows.</param>
/// <param name="SenderClockOffset">
/// This side's wall clock less the peer's, as estimated from the peer's sender reports and the round trip;
/// null before the first report. It places the peer's capture times on this side's clock.
/// </param>
public readonly record struct TransportStatistics(
    CapacityEstimate Capacity,
    double LossRate,
    long PacketsSent,
    long RetransmissionsSent,
    long RetransmissionsRequested,
    long Recovered,
    long KeyframeRequestsSent,
    CircuitBreakerState CircuitBreaker,
    TimeSpan? SenderClockOffset
);

/// <summary>
/// Per-session settings of a transport. A transport derives its own record from this for what only it
/// knows; <c>with</c> keeps the derived type, so a room can set the servers on any transport's options.
/// </summary>
public record MediaTransportOptions
{
    /// <summary>
    /// The connectivity servers: <c>stun:</c> URLs, and <c>turn:</c>/<c>turns:</c> URLs with their
    /// credentials. A room's servers are used when this is empty.
    /// </summary>
    public ImmutableArray<IceServer> IceServers { get; init; } = [];

    /// <summary>
    /// Whether sent video is protected with forward error correction, which repairs loss without a
    /// retransmission round trip at the cost of repair traffic inside the same budget.
    /// </summary>
    public bool ForwardErrorCorrection { get; init; }
}

/// <summary>
/// Carries one session's media between two peers: negotiates over a signaling channel, then sends encoded
/// frames and hands over received ones. It adapts to the path itself (pacing, congestion control, loss
/// recovery) and reports the capacity the media should fit.
/// </summary>
public interface IMediaTransport : IAsyncDisposable
{
    /// <summary>Where the connection stands.</summary>
    TransportState State { get; }

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    event Action<TransportState>? StateChanged;

    /// <summary>The path's capacity now.</summary>
    CapacityEstimate Capacity { get; }

    /// <summary>Raised when the capacity changes.</summary>
    event Action<CapacityEstimate>? CapacityChanged;

    /// <summary>Raised when the peer asks for a keyframe.</summary>
    event Action? KeyframeRequested;

    /// <summary>Raised when the circuit breaker opens, reduces or closes.</summary>
    event Action<CircuitBreakerState>? CircuitBreakerChanged;

    /// <summary>The network path media takes, once one is chosen.</summary>
    MediaRoute? Route { get; }

    /// <summary>Counters and estimates.</summary>
    TransportStatistics Statistics { get; }

    /// <summary>
    /// Negotiates the offer with the peer and connects; completes with what was agreed once media can
    /// flow, and faults when the connection fails first.
    /// </summary>
    /// <param name="offer">What this side offers.</param>
    /// <param name="receiver">Where received media goes.</param>
    /// <param name="cancellationToken">Cancels connecting.</param>
    /// <returns>What both sides agreed.</returns>
    Task<NegotiatedMedia> ConnectAsync(
        MediaOffer offer,
        IReceivedMediaConsumer receiver,
        CancellationToken cancellationToken = default
    );

    /// <summary>The bytes sent so far in a traffic class, for the media-rate allocator.</summary>
    /// <param name="trafficClass">The class.</param>
    /// <returns>The bytes.</returns>
    long SentBytes(TrafficClass trafficClass);

    /// <summary>Sends an encoded video frame; dropped when the transport cannot send yet.</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="capture">When it was captured, on this side's wall clock.</param>
    /// <param name="timing">The frame's encode timing, when it is a timing frame.</param>
    /// <returns>Whether it was queued to send.</returns>
    bool TrySendVideo(in EncodedVideoFrame frame, NtpTime capture, FrameSendTiming? timing);

    /// <summary>Sends an encoded audio packet; dropped when the transport cannot send yet.</summary>
    /// <param name="frame">The packet.</param>
    /// <param name="capture">When its first sample was captured, on this side's wall clock.</param>
    /// <returns>Whether it was queued to send.</returns>
    bool TrySendAudio(in EncodedAudioFrame frame, NtpTime capture);

    /// <summary>Asks the peer for a keyframe; requests in quick succession are combined.</summary>
    void RequestKeyframe();

    /// <summary>Resumes media after the circuit breaker opened, when that is allowed yet.</summary>
    /// <returns>Whether media flows again.</returns>
    bool TryResume();

    /// <summary>
    /// Finds the network path again from scratch, keeping the session and its keys: for WebRTC an ICE
    /// restart, with fresh credentials and candidates exchanged over signaling. For when no known path
    /// works any more; a path that merely changed is followed without it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the restart.</param>
    /// <returns>A task that completes once the restart has been signaled.</returns>
    Task RestartAsync(CancellationToken cancellationToken = default);
}

/// <summary>Makes the transport for each session.</summary>
public interface IMediaTransportFactory
{
    /// <summary>Makes a transport, not yet connected.</summary>
    /// <param name="signaling">The channel negotiation runs over.</param>
    /// <param name="role">Whether this side offers or answers.</param>
    /// <param name="options">The session's transport settings.</param>
    /// <returns>The transport.</returns>
    IMediaTransport Create(
        ISignalingChannel signaling,
        MediaSessionRole role,
        MediaTransportOptions options
    );
}
