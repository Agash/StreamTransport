namespace Agash.StreamTransport.Adaptation;

/// <summary>
/// What a packet carries, which decides its turn at the pacer. Every class spends the same send budget;
/// the order is audio, retransmission, video, FEC repair, then path MTU probes.
/// </summary>
public enum TrafficClass
{
    /// <summary>Audio: small, steady and the first to be heard missing, so it is never held back.</summary>
    Audio,

    /// <summary>A retransmission of a lost packet, which the receiver is waiting on.</summary>
    Retransmission,

    /// <summary>Video.</summary>
    Video,

    /// <summary>Forward error correction repair, worth least once the media it protects has gone.</summary>
    Repair,

    /// <summary>
    /// A path MTU probe (RFC 8899): padding that tests a larger packet size, sent when nothing else waits.
    /// </summary>
    Probe,
}
