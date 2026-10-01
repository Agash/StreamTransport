using System.Net;

namespace Agash.StreamTransport.WebRtc.Ice;

/// <summary>The path a connection's media takes: the selected candidate pair's two ends.</summary>
/// <param name="Local">This side's endpoint.</param>
/// <param name="Remote">The peer's endpoint.</param>
public readonly record struct IcePath(IPEndPoint Local, IPEndPoint Remote);
