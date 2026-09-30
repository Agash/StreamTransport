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
}
