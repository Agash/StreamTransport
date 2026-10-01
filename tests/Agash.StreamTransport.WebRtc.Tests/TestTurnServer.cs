using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Agash.StreamTransport.WebRtc.Stun;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// A small TURN server on loopback for tests: long-term credential challenge, Allocate, Refresh,
/// CreatePermission, ChannelBind, Send and Data indications and ChannelData, over UDP, TCP and TLS. Each
/// allocation relays through its own UDP socket and enforces permissions, as a real server does.
/// </summary>
internal sealed class TestTurnServer : IAsyncDisposable
{
    public const string Realm = "streamtransport.test";
    public const string Username = "user";
    public const string Password = "secret";

    private static readonly byte[] Nonce = Encoding.UTF8.GetBytes("nonce-1");
    private static readonly byte[] FreshNonce = Encoding.UTF8.GetBytes("nonce-2");

    private readonly CancellationTokenSource _stop = new();
    private readonly Socket _udp;
    private readonly TcpListener _tcp;
    private readonly TcpListener? _tls;
    private readonly X509Certificate2? _certificate;
    private readonly ConcurrentDictionary<object, Allocation> _allocations = new();
    private readonly ConcurrentBag<Task> _loops = [];
    private readonly byte[] _key = MD5.HashData(
        Encoding.UTF8.GetBytes($"{Username}:{Realm}:{Password}")
    );
    private int _staleNonces;

    public TestTurnServer(IPAddress address, bool tls = false, int staleNonces = 0)
    {
        _staleNonces = staleNonces;
        _udp = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        _udp.Bind(new IPEndPoint(address, 0));
        _tcp = new TcpListener(address, 0);
        _tcp.Start();
        _loops.Add(UdpLoopAsync());
        _loops.Add(AcceptLoopAsync(_tcp, null));
        if (tls)
        {
            _certificate = SelfSigned();
            _tls = new TcpListener(address, 0);
            _tls.Start();
            _loops.Add(AcceptLoopAsync(_tls, _certificate));
        }
    }

    public IPEndPoint UdpEndPoint => (IPEndPoint)_udp.LocalEndPoint!;

    public IPEndPoint TcpEndPoint => (IPEndPoint)_tcp.LocalEndpoint;

    public IPEndPoint TlsEndPoint => (IPEndPoint)_tls!.LocalEndpoint;

    public int Allocations;
    public int ChannelBinds;
    public int Refreshes;
    public int SendIndications;
    public int ChannelDataFrames;
    public int Deallocations;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _udp.Dispose();
        _tcp.Stop();
        _tls?.Stop();
        foreach (Allocation allocation in _allocations.Values)
        {
            allocation.Relay.Dispose();
        }

        try
        {
            await Task.WhenAll(_loops);
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
        {
            // Shutting down.
        }

        _certificate?.Dispose();
        _stop.Dispose();
    }

    private static X509Certificate2 SelfSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=turn.test", key, HashAlgorithmName.SHA256);
        using X509Certificate2 created = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1)
        );
        // SslStream on Windows needs the key in a persisted form.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null);
    }

    private async Task UdpLoopAsync()
    {
        byte[] buffer = new byte[4096];
        EndPoint any = new IPEndPoint(
            _udp.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any,
            0
        );
        while (!_stop.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _udp.ReceiveFromAsync(buffer, SocketFlags.None, any, _stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            var client = (IPEndPoint)result.RemoteEndPoint;
            await HandleAsync(
                client,
                client,
                buffer.AsMemory(0, result.ReceivedBytes),
                reply => _udp.SendToAsync(reply, SocketFlags.None, client)
            );
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, X509Certificate2? certificate)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = ServeStreamAsync(client, certificate);
        }
    }

    private async Task ServeStreamAsync(TcpClient client, X509Certificate2? certificate)
    {
        using (client)
        {
            Stream stream = client.GetStream();
            if (certificate is not null)
            {
                var tls = new SslStream(stream);
                await tls.AuthenticateAsServerAsync(certificate);
                stream = tls;
            }

            object key = new();
            var writer = new SemaphoreSlim(1, 1);
            var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
            byte[] header = new byte[4];
            byte[] body = new byte[70000];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(header, _stop.Token);
                    int declared = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
                    bool channelData = (header[0] & 0xC0) == 0x40;
                    int rest = channelData ? (declared + 3) & ~3 : 16 + declared;
                    await stream.ReadExactlyAsync(body.AsMemory(0, rest), _stop.Token);
                    byte[] message = [.. header, .. body.AsSpan(0, channelData ? declared : rest)];
                    await HandleAsync(
                        key,
                        remote,
                        message,
                        async reply =>
                        {
                            await writer.WaitAsync();
                            try
                            {
                                await stream.WriteAsync(reply);
                                int pad = (reply.Span[0] & 0xC0) == 0x40 ? (4 - (reply.Length & 3)) & 3 : 0;
                                await stream.WriteAsync(new byte[pad]);
                            }
                            finally
                            {
                                writer.Release();
                            }

                            return reply.Length;
                        }
                    );
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The client went away or the server stopped.
            }
            finally
            {
                if (_allocations.TryRemove(key, out Allocation? allocation))
                {
                    allocation.Relay.Dispose();
                }
            }
        }
    }

    private async Task HandleAsync(
        object key,
        IPEndPoint client,
        ReadOnlyMemory<byte> message,
        Func<ReadOnlyMemory<byte>, ValueTask<int>> reply
    )
    {
        if (message.Length >= 4 && (message.Span[0] & 0xC0) == 0x40)
        {
            ushort channel = BinaryPrimitives.ReadUInt16BigEndian(message.Span);
            int length = BinaryPrimitives.ReadUInt16BigEndian(message.Span[2..]);
            if (_allocations.TryGetValue(key, out Allocation? bound) && bound.Channels.TryGetValue(channel, out IPEndPoint? peer))
            {
                Interlocked.Increment(ref ChannelDataFrames);
                await bound.Relay.SendToAsync(message.Slice(4, length), SocketFlags.None, peer);
            }

            return;
        }

        byte[]? response = Respond(key, client, message.ToArray(), reply, out (byte[] Data, IPEndPoint Peer, Allocation Allocation)? forward);
        if (response is not null)
        {
            await reply(response);
        }

        if (forward is { } send)
        {
            await send.Allocation.Relay.SendToAsync(send.Data, SocketFlags.None, send.Peer);
        }
    }

    private byte[]? Respond(
        object key,
        IPEndPoint client,
        byte[] message,
        Func<ReadOnlyMemory<byte>, ValueTask<int>> reply,
        out (byte[] Data, IPEndPoint Peer, Allocation Allocation)? forward
    )
    {
        forward = null;
        if (!StunMessageReader.TryParse(message, out StunMessageReader request))
        {
            return null;
        }

        _allocations.TryGetValue(key, out Allocation? allocation);
        if (request.Class == StunMessageClass.Indication && request.Method == StunMethod.Send)
        {
            if (
                allocation is not null
                && request.TryGetXorAddress(StunAttributeType.XorPeerAddress, out IPEndPoint peer)
                && request.TryFindAttribute(StunAttributeType.Data, out ReadOnlySpan<byte> data)
                && allocation.Permissions.ContainsKey(peer.Address)
            )
            {
                Interlocked.Increment(ref SendIndications);
                forward = (data.ToArray(), peer, allocation);
            }

            return null;
        }

        if (request.Class != StunMessageClass.Request)
        {
            return null;
        }

        if (!request.TryFindAttribute(StunAttributeType.MessageIntegrity, out _))
        {
            return Error(request, 401, Nonce);
        }

        if (!request.VerifyMessageIntegrity(_key))
        {
            return Error(request, 401, Nonce);
        }

        if (request.Method != StunMethod.Allocate && Interlocked.Decrement(ref _staleNonces) >= 0)
        {
            return Error(request, 438, FreshNonce);
        }

        switch (request.Method)
        {
            case StunMethod.Allocate:
            {
                if (allocation is null)
                {
                    var relay = new Socket(client.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                    relay.Bind(new IPEndPoint(((IPEndPoint)_udp.LocalEndPoint!).Address, 0));
                    allocation = new Allocation(relay);
                    _allocations[key] = allocation;
                    _loops.Add(RelayLoopAsync(allocation, reply));
                    Interlocked.Increment(ref Allocations);
                }

                return Success(request, (ref StunMessageWriter w) =>
                {
                    w.AddXorAddress(StunAttributeType.XorRelayedAddress, (IPEndPoint)allocation.Relay.LocalEndPoint!);
                    w.AddXorMappedAddress(client);
                    w.AddUInt32(StunAttributeType.Lifetime, 600);
                });
            }

            case StunMethod.Refresh:
            {
                Interlocked.Increment(ref Refreshes);
                request.TryGetUInt32(StunAttributeType.Lifetime, out uint lifetime);
                if (lifetime == 0 && _allocations.TryRemove(key, out Allocation? released))
                {
                    Interlocked.Increment(ref Deallocations);
                    released.Relay.Dispose();
                }

                return Success(request, (ref StunMessageWriter w) => w.AddUInt32(StunAttributeType.Lifetime, lifetime));
            }

            case StunMethod.CreatePermission when allocation is not null:
            {
                if (request.TryGetXorAddress(StunAttributeType.XorPeerAddress, out IPEndPoint peer))
                {
                    allocation.Permissions[peer.Address] = true;
                }

                return Success(request, static (ref StunMessageWriter _) => { });
            }

            case StunMethod.ChannelBind when allocation is not null:
            {
                if (
                    !request.TryGetUInt32(StunAttributeType.ChannelNumber, out uint number)
                    || !request.TryGetXorAddress(StunAttributeType.XorPeerAddress, out IPEndPoint peer)
                )
                {
                    return Error(request, 400, Nonce);
                }

                Interlocked.Increment(ref ChannelBinds);
                ushort channel = (ushort)(number >> 16);
                allocation.Channels[channel] = peer;
                allocation.ChannelsByPeer[peer] = channel;
                allocation.Permissions[peer.Address] = true;
                return Success(request, static (ref StunMessageWriter _) => { });
            }

            default:
                return Error(request, 437, Nonce);
        }
    }

    private async Task RelayLoopAsync(Allocation allocation, Func<ReadOnlyMemory<byte>, ValueTask<int>> reply)
    {
        byte[] buffer = new byte[4096];
        EndPoint any = new IPEndPoint(
            allocation.Relay.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any,
            0
        );
        while (!_stop.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await allocation.Relay.ReceiveFromAsync(buffer, SocketFlags.None, any, _stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            var peer = (IPEndPoint)result.RemoteEndPoint;
            if (!allocation.Permissions.ContainsKey(peer.Address))
            {
                continue;
            }

            byte[] frame;
            if (allocation.ChannelsByPeer.TryGetValue(peer, out ushort channel))
            {
                frame = new byte[4 + result.ReceivedBytes];
                BinaryPrimitives.WriteUInt16BigEndian(frame, channel);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)result.ReceivedBytes);
                buffer.AsSpan(0, result.ReceivedBytes).CopyTo(frame.AsSpan(4));
            }
            else
            {
                frame = new byte[result.ReceivedBytes + 64];
                StunMessageWriter writer = new(
                    frame,
                    StunMessageClass.Indication,
                    StunMethod.Data,
                    RandomNumberGenerator.GetBytes(12)
                );
                writer.AddXorAddress(StunAttributeType.XorPeerAddress, peer);
                writer.AddAttribute(StunAttributeType.Data, buffer.AsSpan(0, result.ReceivedBytes));
                frame = frame[..writer.Length];
            }

            try
            {
                await reply(frame);
            }
            catch (Exception exception) when (exception is SocketException or IOException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    private delegate void Attributes(ref StunMessageWriter writer);

    private byte[] Success(StunMessageReader request, Attributes attributes)
    {
        byte[] buffer = new byte[512];
        StunMessageWriter writer = new(buffer, StunMessageClass.SuccessResponse, request.Method, request.TransactionId);
        attributes(ref writer);
        writer.AddMessageIntegrity(_key);
        writer.AddFingerprint();
        return buffer[..writer.Length];
    }

    private static byte[] Error(StunMessageReader request, int code, byte[] nonce)
    {
        byte[] buffer = new byte[512];
        StunMessageWriter writer = new(buffer, StunMessageClass.ErrorResponse, request.Method, request.TransactionId);
        writer.AddErrorCode(code, code == 438 ? "Stale Nonce" : "Unauthorized");
        writer.AddAttribute(StunAttributeType.Realm, Encoding.UTF8.GetBytes(Realm));
        writer.AddAttribute(StunAttributeType.Nonce, nonce);
        writer.AddFingerprint();
        return buffer[..writer.Length];
    }

    private sealed class Allocation(Socket relay)
    {
        public Socket Relay { get; } = relay;

        public ConcurrentDictionary<IPAddress, bool> Permissions { get; } = new();

        public ConcurrentDictionary<ushort, IPEndPoint> Channels { get; } = new();

        public ConcurrentDictionary<IPEndPoint, ushort> ChannelsByPeer { get; } = new();
    }
}
