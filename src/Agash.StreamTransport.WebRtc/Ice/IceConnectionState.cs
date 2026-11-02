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

    /// <summary>
    /// The selected pair stopped receiving: connectivity is lost for now and may come back, or the agent may
    /// move to another pair (the W3C <c>disconnected</c> state). <see cref="Connected"/> again once it
    /// receives or another pair takes over.
    /// </summary>
    Disconnected = 5,
}
