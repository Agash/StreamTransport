namespace Agash.StreamTransport;

/// <summary>The kind of an SDP session description.</summary>
public enum SdpKind
{
    /// <summary>An SDP offer.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("offer")]
    Offer,

    /// <summary>An SDP answer.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("answer")]
    Answer,
}

/// <summary>An SDP session description exchanged during the WebRTC handshake.</summary>
/// <param name="Kind">Offer or answer.</param>
/// <param name="Sdp">The SDP payload.</param>
public readonly record struct SessionDescription(SdpKind Kind, string Sdp);

/// <summary>An ICE candidate exchanged during connectivity establishment.</summary>
/// <param name="Candidate">The candidate string.</param>
/// <param name="SdpMid">The media stream identification, if any.</param>
/// <param name="SdpMLineIndex">The media line index, if any.</param>
public readonly record struct IceCandidateInit(
    string Candidate,
    string? SdpMid,
    int? SdpMLineIndex
);

/// <summary>
/// The out-of-band channel that carries the WebRTC handshake (SDP offer/answer and ICE candidates)
/// between two peers. The transport never assumes how it is delivered - an implementation might use a
/// WebSocket, SignalR, or any other duplex channel. The host application owns reachability (for
/// example exposing a WebSocket endpoint through a tunnel); the transport only sends and receives
/// these messages.
/// </summary>
public interface ISignalingChannel : IAsyncDisposable
{
    /// <summary>Send a session description (offer or answer) to the remote peer.</summary>
    Task SendAsync(SessionDescription description, CancellationToken cancellationToken = default);

    /// <summary>Send a local ICE candidate to the remote peer.</summary>
    Task SendAsync(IceCandidateInit candidate, CancellationToken cancellationToken = default);

    /// <summary>Raised when a session description arrives from the remote peer.</summary>
    event Func<SessionDescription, Task>? DescriptionReceived;

    /// <summary>Raised when an ICE candidate arrives from the remote peer.</summary>
    event Func<IceCandidateInit, Task>? IceCandidateReceived;

    /// <summary>
    /// Whether candidates travel on their own as they are gathered (trickle ICE). A channel that carries
    /// one description each way and nothing else (WHIP, WHEP) says false, and a session then sends its
    /// description once every candidate is in it.
    /// </summary>
    bool SupportsTrickle => true;
}
