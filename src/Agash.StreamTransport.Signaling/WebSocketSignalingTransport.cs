using System.Net.WebSockets;

namespace Agash.StreamTransport;

/// <summary>
/// Carries <see cref="SignalingMessage"/>s over a duplex WebSocket, in the canonical
/// <see cref="SignalingJson"/> format. Symmetric: the relay wraps a server-accepted socket and the
/// room-aware client wraps a <see cref="ClientWebSocket"/>. Implements
/// <see cref="IDuplexSignalingTransport"/>: send via <see cref="SendAsync"/>, pump inbound via
/// <see cref="RunAsync"/> raising <see cref="MessageReceived"/>. Set <paramref name="ownsSocket"/> so
/// disposing the transport closes and disposes the socket (the client owns its socket; the relay handler
/// keeps ownership of the request socket).
/// </summary>
public sealed class WebSocketSignalingTransport(WebSocket socket, bool ownsSocket = false) : IDuplexSignalingTransport
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Raised for each inbound signaling message.</summary>
    public event Func<SignalingMessage, Task>? MessageReceived;

    /// <inheritdoc/>
    public async ValueTask SendAsync(SignalingMessage message, CancellationToken cancellationToken = default)
    {
        byte[] bytes = SignalingJson.SerializeToUtf8Bytes(message);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Pump inbound frames until the socket closes or the token is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                try
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            SignalingMessage? parsed;
            try
            {
                parsed = SignalingJson.Deserialize(message.GetBuffer().AsSpan(0, (int)message.Length));
            }
            catch (System.Text.Json.JsonException)
            {
                continue; // drop malformed frame, keep the connection.
            }

            if (parsed is not null && MessageReceived is { } handler)
            {
                await handler(parsed).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (ownsSocket)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Best-effort close.
            }

            socket.Dispose();
        }

        _sendLock.Dispose();
    }
}
