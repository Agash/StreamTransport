namespace Agash.StreamTransport.Linux.PipeWire;

/// <summary>Where a PipeWire audio stream connects.</summary>
public sealed record PipeWireAudioOptions
{
    /// <summary>The node to connect to, by id; null lets the session manager choose.</summary>
    public uint? TargetNodeId { get; init; }

    /// <summary>The node to connect to, by name or serial; null lets the session manager choose.</summary>
    public string? TargetObject { get; init; }

    /// <summary>
    /// Let the session manager route the stream. Off leaves it unlinked, for an application that links
    /// it deliberately.
    /// </summary>
    public bool AutoConnect { get; init; } = true;

    /// <summary>The name the stream has in the graph.</summary>
    public string NodeName { get; init; } = "StreamTransport audio";

    /// <summary>
    /// For a source: capture what an output plays, through its monitor, instead of an input. With no
    /// target the session manager picks the default output. A sink plays as usual.
    /// </summary>
    public bool CaptureOutput { get; init; }
}
