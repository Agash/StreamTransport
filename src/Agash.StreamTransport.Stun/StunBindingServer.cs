using System.Net;
using System.Net.Sockets;
using Agash.StreamTransport.WebRtc.Stun;

namespace Agash.StreamTransport.Stun;

/// <summary>
/// A minimal, embeddable STUN server: it answers RFC 5389 <c>Binding</c> requests with the sender's
/// reflexive transport address (XOR-MAPPED-ADDRESS), which is all WebRTC ICE needs to gather server-
/// reflexive candidates. Single UDP port, no RFC 3489 NAT-type detection. Cross-platform.
/// </summary>
/// <remarks>
/// This is what the light agent "ships with": run one on a reachable UDP port (or co-host it with the
/// relay) so peers can discover their public address without depending on a third-party STUN service.
/// For media relaying through symmetric NAT you still need TURN - point an
/// <see cref="IIceServerProvider"/> at an external coturn for that.
/// </remarks>
public sealed class StunBindingServer : IAsyncDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>
    /// Create a STUN server bound to <paramref name="listenEndPoint"/>: 0.0.0.0:3478 for IPv4 only, or
    /// [::]:3478 for IPv6 and IPv4 on one socket.
    /// </summary>
    public StunBindingServer(IPEndPoint listenEndPoint)
    {
        ArgumentNullException.ThrowIfNull(listenEndPoint);
        Socket socket = new(listenEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (listenEndPoint.Address.Equals(IPAddress.IPv6Any))
            {
                socket.DualMode = true;
            }

            socket.Bind(listenEndPoint);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _udp = new UdpClient { Client = socket };
        ListenEndPoint = (IPEndPoint)socket.LocalEndPoint!;
    }

    /// <summary>
    /// Every address this host has on IPv6 and IPv4, port <paramref name="port"/>; IPv4 only where the
    /// host has no IPv6.
    /// </summary>
    public static IPEndPoint AnyAddress(int port) =>
        new(Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any, port);

    /// <summary>The bound local endpoint (the port is resolved when binding to port 0).</summary>
    public IPEndPoint ListenEndPoint { get; }

    /// <summary>Start answering binding requests until disposed.</summary>
    public void Start() => _loop ??= Task.Run(() => ReceiveLoopAsync(_cts.Token));

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                // Transient ICMP port-unreachable etc.; keep serving.
                continue;
            }

            byte[]? response = TryBuildBindingResponse(received.Buffer, received.RemoteEndPoint);
            if (response is not null)
            {
                try
                {
                    await _udp.SendAsync(response, response.Length, received.RemoteEndPoint)
                        .ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // Client vanished; ignore.
                }
            }
        }
    }

    private static byte[]? TryBuildBindingResponse(byte[] datagram, IPEndPoint from)
    {
        if (
            !StunMessageReader.TryParse(datagram, out StunMessageReader request)
            || request.Class != StunMessageClass.Request
            || request.Method != StunMethod.Binding
        )
        {
            return null;
        }

        byte[] response = new byte[64];
        var writer = new StunMessageWriter(
            response,
            StunMessageClass.SuccessResponse,
            StunMethod.Binding,
            request.TransactionId
        );
        // A dual-stack socket sees IPv4 senders as IPv4-mapped IPv6; they reflect as the IPv4 they are.
        writer.AddXorMappedAddress(
            from.Address.IsIPv4MappedToIPv6
                ? new IPEndPoint(from.Address.MapToIPv4(), from.Port)
                : from
        );
        // No message-integrity key; append a FINGERPRINT so clients can validate the response.
        writer.AddFingerprint();
        return response[..writer.Length];
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_loop is not null)
            {
                await _loop.ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Loop teardown races are benign.
        }

        _udp.Dispose();
        _cts.Dispose();
    }
}
