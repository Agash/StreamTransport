namespace Agash.StreamTransport.WebRtc.Ice;

/// <summary>
/// The state of an <see cref="IceAgent"/>, following the ICE transport states of the W3C WebRTC API
/// (a simplified subset sufficient for a point-to-point agent).
/// </summary>
public enum IceConnectionState
{
    /// <summary>Created, not yet gathering or checking.</summary>
    New = 0,

    /// <summary>Gathering candidates and/or running connectivity checks.</summary>
    Checking = 1,

    /// <summary>A candidate pair has been nominated and is usable.</summary>
    Connected = 2,

    /// <summary>Connectivity checks exhausted, or consent to the selected pair was lost.</summary>
    Failed = 3,

    /// <summary>The agent has been closed.</summary>
    Closed = 4,
}
