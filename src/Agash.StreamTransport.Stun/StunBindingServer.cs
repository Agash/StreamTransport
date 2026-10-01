using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using Agash.StreamTransport.WebRtc.Stun;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
public sealed partial class StunBindingServer : IAsyncDisposable
{
    /// <summary>The meter the server reports on: <c>Agash.StreamTransport.Stun</c>.</summary>
    public const string MeterName = "Agash.StreamTransport.Stun";

    private readonly UdpClient _udp;
    private readonly ILogger _logger;
    private readonly Meter? _ownMeter;
    private readonly Counter<long> _requests;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>
    /// Create a STUN server bound to <paramref name="listenEndPoint"/>: 0.0.0.0:3478 for IPv4 only, or
    /// [::]:3478 for IPv6 and IPv4 on one socket.
    /// </summary>
    /// <param name="listenEndPoint">Where to listen.</param>
    /// <param name="logger">The logging; none when null.</param>
    /// <param name="meterFactory">
    /// Where the metrics' meter comes from (<see cref="MeterName"/>); a meter of the server's own when null.
    /// </param>
    public StunBindingServer(
        IPEndPoint listenEndPoint,
        ILogger<StunBindingServer>? logger = null,
        IMeterFactory? meterFactory = null
    )
    {
        ArgumentNullException.ThrowIfNull(listenEndPoint);
        _logger = logger ?? NullLogger<StunBindingServer>.Instance;
        Meter meter = meterFactory?.Create(MeterName) ?? (_ownMeter = new Meter(MeterName));
        _requests = meter.CreateCounter<long>(
            "streamtransport.stun.requests",
            "{request}",
            "Datagrams received, by outcome: answered, ignored (not a binding request) or failed (the answer could not be sent)."
        );
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
    public void Start()
    {
        if (_loop is null)
        {
            LogListening(ListenEndPoint);
            _loop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }
    }

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
                // Deliberately not logged: the server is being disposed.
                return;
            }
            catch (SocketException exception)
            {
                // An ICMP port-unreachable for an earlier answer surfaces here; the server keeps serving.
                LogReceiveFailed(exception, exception.SocketErrorCode);
                continue;
            }

            byte[]? response = TryBuildBindingResponse(received.Buffer, received.RemoteEndPoint);
            if (response is null)
            {
                _requests.Add(
                    1,
                    new KeyValuePair<string, object?>("streamtransport.outcome", "ignored")
                );
                continue;
            }

            try
            {
                await _udp.SendAsync(response, response.Length, received.RemoteEndPoint)
                    .ConfigureAwait(false);
                _requests.Add(
                    1,
                    new KeyValuePair<string, object?>("streamtransport.outcome", "answered")
                );
            }
            catch (SocketException exception)
            {
                // The client is gone; there is nobody to answer.
                _requests.Add(
                    1,
                    new KeyValuePair<string, object?>("streamtransport.outcome", "failed")
                );
                LogAnswerFailed(exception, exception.SocketErrorCode);
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
        _ownMeter?.Dispose();
    }

    [LoggerMessage(
        EventId = 2850,
        Level = LogLevel.Information,
        Message = "STUN server answering binding requests on {EndPoint}."
    )]
    private partial void LogListening(IPEndPoint endPoint);

    [LoggerMessage(
        EventId = 2851,
        Level = LogLevel.Debug,
        Message = "Receiving failed ({Error}); still serving."
    )]
    private partial void LogReceiveFailed(Exception exception, SocketError error);

    [LoggerMessage(
        EventId = 2852,
        Level = LogLevel.Debug,
        Message = "Sending a binding answer failed ({Error})."
    )]
    private partial void LogAnswerFailed(Exception exception, SocketError error);
}
