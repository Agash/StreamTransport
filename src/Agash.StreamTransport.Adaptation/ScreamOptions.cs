namespace Agash.StreamTransport.Adaptation;

/// <summary>How the path signals congestion with ECN, which decides the response to a CE mark.</summary>
public enum EcnMode
{
    /// <summary>The path does not carry ECN; congestion shows as loss and delay only.</summary>
    None,

    /// <summary>Classic ECN (RFC 3168): a CE mark backs off by a fixed factor, like a loss but less.</summary>
    Classic,

    /// <summary>L4S (RFC 9331): the window shrinks in proportion to the fraction of packets marked.</summary>
    L4s,
}

/// <summary>
/// Tunables for <see cref="ScreamCongestionController"/>: SCReAMv2's constants
/// (draft-ietf-ccwg-rfc8298bis-screamv2-01 section 4), with the draft's recommended values as defaults.
/// </summary>
public sealed class ScreamOptions
{
    /// <summary>The least target bitrate (TARGET_BITRATE_MIN), bits per second.</summary>
    public long MinBitrateBps { get; set; } = 150_000;

    /// <summary>The greatest target bitrate (TARGET_BITRATE_MAX), bits per second.</summary>
    public long MaxBitrateBps { get; set; } = 10_000_000;

    /// <summary>The target bitrate before the first feedback, bits per second.</summary>
    public long StartBitrateBps { get; set; } = 1_000_000;

    /// <summary>How the path's ECN marks are read.</summary>
    public EcnMode Ecn { get; set; } = EcnMode.L4s;

    /// <summary>The queue delay target's floor (QDELAY_TARGET_LO), milliseconds.</summary>
    public int QueueDelayTargetMs { get; set; } = 60;

    /// <summary>
    /// The queue delay target's ceiling (QDELAY_TARGET_HI), milliseconds, which the target may rise to
    /// when loss-based flows compete for the bottleneck.
    /// </summary>
    public int QueueDelayTargetMaxMs { get; set; } = 400;

    /// <summary>
    /// Whether the queue delay target follows competing loss-based traffic (section 4.5.4). Turning it off
    /// is only safe where no such traffic shares the bottleneck, such as a QoS bearer.
    /// </summary>
    public bool CompetingFlowCompensation { get; set; } = true;

    /// <summary>
    /// Whether delay jitter is filtered (REDUCE_JITTER, section 4.2.1.4): less reaction to short delay
    /// variations from link-layer scheduling, which suits cellular links.
    /// </summary>
    public bool ReduceJitter { get; set; } = true;

    /// <summary>The reference window's floor (MIN_REF_WND), bytes.</summary>
    public int MinReferenceWindow { get; set; } = 3000;

    /// <summary>The largest data unit (MSS), bytes.</summary>
    public int MaxSegmentSize { get; set; } = 1200;

    /// <summary>The window scale on a loss event (BETA_LOSS).</summary>
    public double LossBeta { get; set; } = 0.7;

    /// <summary>The window scale on a classic ECN event (BETA_ECN).</summary>
    public double EcnBeta { get; set; } = 0.8;

    /// <summary>The average loss rate above which loss backs off (LOSS_RATE_THRESHOLD, section 4.5.2).</summary>
    public double LossRateThreshold { get; set; } = 0.01;

    /// <summary>The fraction of the window that it may grow by per round trip (MUL_INCREASE_FACTOR).</summary>
    public double MultiplicativeIncreaseFactor { get; set; } = 0.02;

    /// <summary>
    /// The virtual round trip (VIRTUAL_RTT), milliseconds: flows with shorter round trips share an L4S
    /// bottleneck as if they had this one.
    /// </summary>
    public int VirtualRttMs { get; set; } = 25;

    /// <summary>Round trips after congestion over which growth stays cautious (POST_CONGESTION_DELAY_RTT).</summary>
    public int PostCongestionDelayRtts { get; set; } = 100;

    /// <summary>Headroom of the pacing rate over the target (PACKET_PACING_HEADROOM).</summary>
    public double PacingHeadroom { get; set; } = 1.5;

    /// <summary>
    /// How much faster pacing may run as the target nears the maximum (MAX_RELAXED_PACING_FACTOR), so frames
    /// leave quickly on an uncongested path.
    /// </summary>
    public double MaxRelaxedPacingFactor { get; set; } = 4.0;
}
