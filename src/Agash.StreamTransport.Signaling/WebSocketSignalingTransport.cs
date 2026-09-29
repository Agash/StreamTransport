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
public sealed class WebSocketSignalingTransport(WebSocket socket, bool ownsSocket = false)
    : IDuplexSignalingTransport
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Raised for each inbound signaling message.</summary>
    public event Func<SignalingMessage, Task>? MessageReceived;

    /// <inheritdoc/>
    public async ValueTask SendAsync(
        SignalingMessage message,
        CancellationToken cancellationToken = default
    )
    {
        byte[] bytes = SignalingJson.SerializeToUtf8Bytes(message);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket
                .SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
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
                    result = await socket
                        .ReceiveAsync(buffer, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await AnswerCloseAsync().ConfigureAwait(false);
                    return;
                }

                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            SignalingMessage? parsed;
            try
            {
                parsed = SignalingJson.Deserialize(
                    message.GetBuffer().AsSpan(0, (int)message.Length)
                );
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

    // Completes a close the peer started. Without the reply the peer's CloseAsync waits for a frame
    // that never comes.
    private async Task AnswerCloseAsync()
    {
        if (socket.State != WebSocketState.CloseReceived)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(CloseTimeout);
        try
        {
            await socket
                .CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // Deliberately not logged: the peer has already closed; failing to acknowledge it only
            // means its close handshake ends by its own timeout.
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (ownsSocket)
        {
            // A peer that never answers the close frame must not hold disposal: give the handshake a
            // moment, then drop the connection.
            using var timeout = new CancellationTokenSource(CloseTimeout);
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket
                        .CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // Deliberately not logged: the connection is being torn down either way, and an
                // unanswered or failed close changes nothing for the caller.
                socket.Abort();
            }

            socket.Dispose();
        }

        _sendLock.Dispose();
    }
}
