namespace Agash.StreamTransport.Adaptation;

/// <summary>The Differentiated Services codepoints WebRTC uses (RFC 8837 section 5, after RFC 4594).</summary>
public enum DiffServCodepoint : byte
{
    /// <summary>Default forwarding, best effort (RFC 2474).</summary>
    Default = 0,

    /// <summary>Lower effort, less than best effort (RFC 8622).</summary>
    LowerEffort = 1,

    /// <summary>Assured forwarding class 1, low drop precedence: high-priority data.</summary>
    Af11 = 10,

    /// <summary>Assured forwarding class 2, low drop precedence: high-priority data.</summary>
    Af21 = 18,

    /// <summary>Assured forwarding class 4, low drop precedence: high-priority interactive video.</summary>
    Af41 = 34,

    /// <summary>Assured forwarding class 4, medium drop precedence.</summary>
    Af42 = 36,

    /// <summary>Assured forwarding class 4, high drop precedence: dropped first within the class.</summary>
    Af43 = 38,

    /// <summary>Expedited forwarding (RFC 3246): audio.</summary>
    Expedited = 46,
}

/// <summary>How important an application's media is relative to its other traffic (RFC 8837 section 5).</summary>
public enum NetworkPriority
{
    /// <summary>Less than best effort.</summary>
    VeryLow,

    /// <summary>Best effort: no marking.</summary>
    Low,

    /// <summary>Audio expedited, video assured with medium drop precedence.</summary>
    Medium,

    /// <summary>Audio expedited, video assured with low drop precedence.</summary>
    High,
}

/// <summary>
/// The IP traffic class byte of a datagram (the IPv4 TOS or IPv6 Traffic Class field): its Differentiated
/// Services codepoint in the upper six bits and its ECN codepoint in the lower two (RFC 2474, RFC 3168).
/// </summary>
/// <param name="DiffServ">The DSCP.</param>
/// <param name="Ecn">The ECN codepoint.</param>
public readonly record struct IpMarking(
    DiffServCodepoint DiffServ,
    EcnCodepoint Ecn = EcnCodepoint.NotEct
)
{
    /// <summary>The field's value.</summary>
    public byte Value => (byte)(((byte)DiffServ << 2) | ((byte)Ecn & 0x03));

    /// <summary>The marking a traffic class byte holds.</summary>
    /// <param name="trafficClass">The byte.</param>
    /// <returns>The marking.</returns>
    public static IpMarking FromTrafficClass(byte trafficClass) =>
        new((DiffServCodepoint)(trafficClass >> 2), (EcnCodepoint)(trafficClass & 0x03));

    /// <summary>
    /// The DSCP RFC 8837 table 1 gives a packet of an interactive WebRTC session: audio expedited; video
    /// assured, and its repairs and probes at the class's highest drop precedence, so a congested hop drops
    /// them before the media.
    /// </summary>
    /// <param name="traffic">What the packet carries.</param>
    /// <param name="priority">The application's priority for the flow.</param>
    /// <returns>The codepoint.</returns>
    public static DiffServCodepoint DiffServFor(TrafficClass traffic, NetworkPriority priority) =>
        priority switch
        {
            NetworkPriority.VeryLow => DiffServCodepoint.LowerEffort,
            NetworkPriority.Low => DiffServCodepoint.Default,
            _ when traffic == TrafficClass.Audio => DiffServCodepoint.Expedited,
            _ when traffic is TrafficClass.Repair or TrafficClass.Probe => DiffServCodepoint.Af43,
            NetworkPriority.High => DiffServCodepoint.Af41,
            _ => DiffServCodepoint.Af42,
        };
}
