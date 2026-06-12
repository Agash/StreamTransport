using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agash.StreamTransport;

/// <summary>
/// An <see cref="ISignalingChannel"/> over a duplex WebSocket. Handshake messages are small JSON
/// envelopes. Uses only <see cref="System.Net.WebSockets"/> from the BCL, so it works with a client
/// <see cref="ClientWebSocket"/> or a server-accepted <see cref="WebSocket"/> - the host wires
/// reachability (for example exposing the endpoint through a tunnel). Call <see cref="RunAsync"/> to
/// pump incoming messages.
/// </summary>
public sealed class WebSocketSignalingChannel : ISignalingChannel
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Create a signaling channel over an already-connected WebSocket.</summary>
    public WebSocketSignalingChannel(WebSocket socket) => _socket = socket;

    /// <inheritdoc/>
    public event Func<SessionDescription, Task>? DescriptionReceived;

    /// <inheritdoc/>
    public event Func<IceCandidate, Task>? IceCandidateReceived;

    /// <inheritdoc/>
    public Task SendAsync(SessionDescription description, CancellationToken cancellationToken = default) =>
        SendEnvelopeAsync(
            new SignalEnvelope
            {
                Type = description.Kind == SdpKind.Offer ? "offer" : "answer",
                Sdp = description.Sdp,
            },
            cancellationToken);

    /// <inheritdoc/>
    public Task SendAsync(IceCandidate candidate, CancellationToken cancellationToken = default) =>
        SendEnvelopeAsync(
            new SignalEnvelope
            {
                Type = "ice",
                Candidate = candidate.Candidate,
                SdpMid = candidate.SdpMid,
                SdpMLineIndex = candidate.SdpMLineIndex,
            },
            cancellationToken);

    /// <summary>Pump incoming messages until the socket closes or the token is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[16 * 1024];
        while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            SignalEnvelope? envelope = JsonSerializer.Deserialize(
                message.ToArray(), SignalEnvelopeContext.Default.SignalEnvelope);
            if (envelope is not null) await DispatchAsync(envelope).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(SignalEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case "offer" when DescriptionReceived is { } handler:
                await handler(new SessionDescription(SdpKind.Offer, envelope.Sdp ?? string.Empty)).ConfigureAwait(false);
                break;
            case "answer" when DescriptionReceived is { } handler:
                await handler(new SessionDescription(SdpKind.Answer, envelope.Sdp ?? string.Empty)).ConfigureAwait(false);
                break;
            case "ice" when IceCandidateReceived is { } handler:
                await handler(new IceCandidate(envelope.Candidate ?? string.Empty, envelope.SdpMid, envelope.SdpMLineIndex)).ConfigureAwait(false);
                break;
            default:
                break;
        }
    }

    private async Task SendEnvelopeAsync(SignalEnvelope envelope, CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, SignalEnvelopeContext.Default.SignalEnvelope);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>JSON envelope for a signaling message on the wire.</summary>
internal sealed class SignalEnvelope
{
    public string Type { get; set; } = string.Empty;
    public string? Sdp { get; set; }
    public string? Candidate { get; set; }
    public string? SdpMid { get; set; }
    public int? SdpMLineIndex { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SignalEnvelope))]
internal sealed partial class SignalEnvelopeContext : JsonSerializerContext;
