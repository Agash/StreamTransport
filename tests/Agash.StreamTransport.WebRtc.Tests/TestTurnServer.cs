using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Agash.StreamTransport.Stun;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// A small TURN server on loopback for tests: long-term credential challenge, Allocate, Refresh,
/// CreatePermission, ChannelBind, Send and Data indications and ChannelData, over UDP, TCP and TLS. Each
/// allocation relays through its own UDP socket and enforces permissions, as a real server does.
/// </summary>
/// <summary>The long-term credential variant a <see cref="TestTurnServer"/> challenges with (RFC 8489 section 9.2).</summary>
public enum TurnSecurity
{
    /// <summary>MD5 keys and MESSAGE-INTEGRITY, with no nonce cookie.</summary>
    Classic,

    /// <summary>A nonce cookie with the password-algorithms bit and the list SHA-256, MD5.</summary>
    Sha256,

    /// <summary>As <see cref="Sha256"/>, with username anonymity: requests carry USERHASH.</summary>
    Sha256Anonymous,

    /// <summary>A cookie announcing password algorithms with the list missing, as after a bid-down.</summary>
    StrippedAlgorithms,

    /// <summary>A list of password algorithms with none the client supports.</summary>
    UnknownAlgorithm,
}

internal sealed class TestTurnServer : IAsyncDisposable
{
    public const string Realm = "streamtransport.test";
    public const string Username = "user";
    public const string Password = "secret";

    private readonly byte[] Nonce;
    private readonly byte[] FreshNonce;
    private readonly TurnSecurity _security;
    private readonly byte[] _algorithms;

    private readonly CancellationTokenSource _stop = new();
    private readonly Socket _udp;
    private readonly TcpListener _tcp;
    private readonly TcpListener? _tls;
    private readonly X509Certificate2? _certificate;
    private readonly ConcurrentDictionary<object, Allocation> _allocations = new();
    private readonly ConcurrentBag<Task> _loops = [];
    private readonly byte[] _key;
    private int _staleNonces;
    private int _mismatches;
    private readonly uint _lifetime;
    private readonly IPEndPoint? _alternate;

    /// <param name="address">Where to listen.</param>
    /// <param name="tls">Whether to listen for TLS as well.</param>
    /// <param name="staleNonces">How many requests after the Allocate to answer 438 first.</param>
    /// <param name="mismatches">How many Allocates to answer 437, as for a 5-tuple in use.</param>
    /// <param name="lifetime">The lifetime to grant allocations, in seconds.</param>
    /// <param name="alternate">Answers every Allocate 300 with this ALTERNATE-SERVER.</param>
    /// <param name="security">The long-term credential variant to challenge with.</param>
    public TestTurnServer(
        IPAddress address,
        bool tls = false,
        int staleNonces = 0,
        int mismatches = 0,
        uint lifetime = 600,
        IPEndPoint? alternate = null,
        TurnSecurity security = TurnSecurity.Classic
    )
    {
        _security = security;
        // RFC 8489 section 9.2: "obMatJos2" and the base64 of the feature bits.
        string cookie = security switch
        {
            TurnSecurity.Classic => "",
            TurnSecurity.Sha256Anonymous => "obMatJos2wAAA",
            _ => "obMatJos2gAAA",
        };
        Nonce = Encoding.UTF8.GetBytes(cookie + "nonce-1");
        FreshNonce = Encoding.UTF8.GetBytes(cookie + "nonce-2");
        _algorithms = security switch
        {
            TurnSecurity.Sha256 or TurnSecurity.Sha256Anonymous => [0, 2, 0, 0, 0, 1, 0, 0],
            TurnSecurity.UnknownAlgorithm => [0x80, 0x01, 0, 0],
            _ => [],
        };
        _key = StunLongTermCredential.Key(
            _algorithms.Length > 0 ? StunPasswordAlgorithm.Sha256 : StunPasswordAlgorithm.Md5,
            Username,
            Encoding.UTF8.GetBytes(Realm),
            Password
        );
        _staleNonces = staleNonces;
        _mismatches = mismatches;
        _lifetime = lifetime;
        _alternate = alternate;
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
    public int AllocateRequests;
    public int CreatePermissions;
    public int RefusedSends;
    public int ChannelBinds;
    public int Refreshes;
    public int SendIndications;
    public int ChannelDataFrames;
    public int Deallocations;

    private readonly TaskCompletionSource _channelBound = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource _channelData = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource _channelRebound = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource _refreshed = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource _deallocated = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    /// <summary>Completes when the server has accepted its first ChannelBind.</summary>
    public Task WhenChannelBound => _channelBound.Task;

    /// <summary>Completes when the server has received its first ChannelData frame.</summary>
    public Task WhenChannelData => _channelData.Task;

    /// <summary>Completes when the server has accepted a second ChannelBind.</summary>
    public Task WhenRefreshedBinding => _channelRebound.Task;

    /// <summary>Completes when the server has accepted its first Refresh.</summary>
    public Task WhenRefreshed => _refreshed.Task;

    /// <summary>Completes when the server has released an allocation.</summary>
    public Task WhenDeallocated => _deallocated.Task;

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
        catch (Exception exception)
            when (exception
                    is OperationCanceledException
                        or SocketException
                        or ObjectDisposedException
                        or IOException
            )
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
            catch (Exception exception)
                when (exception is OperationCanceledException or ObjectDisposedException)
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
            catch (Exception exception)
                when (exception
                        is OperationCanceledException
                            or ObjectDisposedException
                            or SocketException
                )
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
                                int pad =
                                    (reply.Span[0] & 0xC0) == 0x40
                                        ? (4 - (reply.Length & 3)) & 3
                                        : 0;
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
            catch (Exception exception)
                when (exception
                        is OperationCanceledException
                            or IOException
                            or ObjectDisposedException
                )
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
            if (
                _allocations.TryGetValue(key, out Allocation? bound)
                && bound.Channels.TryGetValue(channel, out IPEndPoint? peer)
            )
            {
                Interlocked.Increment(ref ChannelDataFrames);
                _ = _channelData.TrySetResult();
                await bound.Relay.SendToAsync(message.Slice(4, length), SocketFlags.None, peer);
            }

            return;
        }

        byte[]? response = Respond(
            key,
            client,
            message.ToArray(),
            reply,
            out (byte[] Data, IPEndPoint Peer, Allocation Allocation)? forward
        );
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
            else
            {
                // RFC 8656 section 11.2: no permission, no relaying.
                Interlocked.Increment(ref RefusedSends);
            }

            return null;
        }

        if (request.Class != StunMessageClass.Request)
        {
            return null;
        }

        if (request.Method == StunMethod.Allocate)
        {
            Interlocked.Increment(ref AllocateRequests);
            if (_alternate is { } alternate)
            {
                return Error(
                    request,
                    300,
                    Nonce,
                    (ref StunMessageWriter w) =>
                        w.AddAddress(StunAttributeType.AlternateServer, alternate)
                );
            }
        }

        if (!Authenticated(request))
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
                if (Interlocked.Decrement(ref _mismatches) >= 0)
                {
                    return Error(request, 437, Nonce);
                }

                if (allocation is null)
                {
                    var relay = new Socket(
                        client.AddressFamily,
                        SocketType.Dgram,
                        ProtocolType.Udp
                    );
                    relay.Bind(new IPEndPoint(((IPEndPoint)_udp.LocalEndPoint!).Address, 0));
                    allocation = new Allocation(relay, reply);
                    _allocations[key] = allocation;
                    _loops.Add(RelayLoopAsync(allocation, reply));
                    Interlocked.Increment(ref Allocations);
                }

                return Success(
                    request,
                    (ref StunMessageWriter w) =>
                    {
                        w.AddXorAddress(
                            StunAttributeType.XorRelayedAddress,
                            (IPEndPoint)allocation.Relay.LocalEndPoint!
                        );
                        w.AddXorMappedAddress(client);
                        w.AddUInt32(StunAttributeType.Lifetime, _lifetime);
                    }
                );
            }

            // RFC 8656 section 8.2: a Refresh for no allocation is a 437.
            case StunMethod.Refresh when allocation is null:
                return Error(request, 437, Nonce);

            case StunMethod.Refresh:
            {
                Interlocked.Increment(ref Refreshes);
                _ = _refreshed.TrySetResult();
                request.TryGetUInt32(StunAttributeType.Lifetime, out uint lifetime);
                if (lifetime == 0 && _allocations.TryRemove(key, out Allocation? released))
                {
                    Interlocked.Increment(ref Deallocations);
                    _ = _deallocated.TrySetResult();
                    released.Relay.Dispose();
                }

                uint granted = lifetime == 0 ? 0 : _lifetime;
                return Success(
                    request,
                    (ref StunMessageWriter w) => w.AddUInt32(StunAttributeType.Lifetime, granted)
                );
            }

            case StunMethod.CreatePermission when allocation is not null:
            {
                Interlocked.Increment(ref CreatePermissions);
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
                    || !request.TryGetXorAddress(
                        StunAttributeType.XorPeerAddress,
                        out IPEndPoint peer
                    )
                )
                {
                    return Error(request, 400, Nonce);
                }

                if (Interlocked.Increment(ref ChannelBinds) >= 2)
                {
                    _ = _channelRebound.TrySetResult();
                }

                _ = _channelBound.TrySetResult();
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

    private async Task RelayLoopAsync(
        Allocation allocation,
        Func<ReadOnlyMemory<byte>, ValueTask<int>> reply
    )
    {
        byte[] buffer = new byte[4096];
        EndPoint any = new IPEndPoint(
            allocation.Relay.AddressFamily == AddressFamily.InterNetworkV6
                ? IPAddress.IPv6Any
                : IPAddress.Any,
            0
        );
        while (!_stop.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await allocation.Relay.ReceiveFromAsync(
                    buffer,
                    SocketFlags.None,
                    any,
                    _stop.Token
                );
            }
            catch (Exception exception)
                when (exception is OperationCanceledException or ObjectDisposedException)
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
                BinaryPrimitives.WriteUInt16BigEndian(
                    frame.AsSpan(2),
                    (ushort)result.ReceivedBytes
                );
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
            catch (Exception exception)
                when (exception is SocketException or IOException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    private delegate void Attributes(ref StunMessageWriter writer);

    /// <summary>Authenticated requests that carried MESSAGE-INTEGRITY-SHA256.</summary>
    public int Sha256Requests;

    /// <summary>Authenticated requests that carried USERHASH.</summary>
    public int UserhashRequests;

    // RFC 8489 section 9.2.4: the integrity attribute the variant calls for, USERHASH when usernames are
    // anonymous, and the PASSWORD-ALGORITHMS echoed with a PASSWORD-ALGORITHM from it.
    private bool Authenticated(StunMessageReader request)
    {
        if (_algorithms.Length == 0)
        {
            return request.TryFindAttribute(StunAttributeType.Username, out _)
                && request.VerifyMessageIntegrity(_key);
        }

        if (
            !request.TryFindAttribute(
                StunAttributeType.PasswordAlgorithms,
                out ReadOnlySpan<byte> echoed
            )
            || !echoed.SequenceEqual(_algorithms)
            || !request.TryFindAttribute(
                StunAttributeType.PasswordAlgorithm,
                out ReadOnlySpan<byte> chosen
            )
            || !chosen.SequenceEqual(_algorithms.AsSpan(0, 4))
            || request.TryFindAttribute(StunAttributeType.MessageIntegrity, out _)
            || !request.VerifyMessageIntegritySha256(_key)
        )
        {
            return false;
        }

        if (_security == TurnSecurity.Sha256Anonymous)
        {
            Span<byte> userhash = stackalloc byte[32];
            StunLongTermCredential.Userhash(Username, Encoding.UTF8.GetBytes(Realm), userhash);
            if (
                !request.TryFindAttribute(StunAttributeType.Userhash, out ReadOnlySpan<byte> sent)
                || !sent.SequenceEqual(userhash)
            )
            {
                return false;
            }

            Interlocked.Increment(ref UserhashRequests);
        }

        Interlocked.Increment(ref Sha256Requests);
        return true;
    }

    private byte[] Success(StunMessageReader request, Attributes attributes)
    {
        byte[] buffer = new byte[512];
        StunMessageWriter writer = new(
            buffer,
            StunMessageClass.SuccessResponse,
            request.Method,
            request.TransactionId
        );
        attributes(ref writer);
        if (_algorithms.Length > 0)
        {
            writer.AddMessageIntegritySha256(_key);
        }
        else
        {
            writer.AddMessageIntegrity(_key);
        }

        writer.AddFingerprint();
        return buffer[..writer.Length];
    }

    private byte[] Error(
        StunMessageReader request,
        int code,
        byte[] nonce,
        Attributes? extra = null
    )
    {
        byte[] buffer = new byte[512];
        StunMessageWriter writer = new(
            buffer,
            StunMessageClass.ErrorResponse,
            request.Method,
            request.TransactionId
        );
        writer.AddErrorCode(code, code == 438 ? "Stale Nonce" : "Unauthorized");
        writer.AddAttribute(StunAttributeType.Realm, Encoding.UTF8.GetBytes(Realm));
        writer.AddAttribute(StunAttributeType.Nonce, nonce);
        if (code is 401 or 438 && _algorithms.Length > 0)
        {
            writer.AddAttribute(StunAttributeType.PasswordAlgorithms, _algorithms);
        }

        extra?.Invoke(ref writer);
        writer.AddFingerprint();
        return buffer[..writer.Length];
    }

    /// <summary>
    /// Sends every client a Data indication from <paramref name="peer"/>, permission or not, as a
    /// misbehaving server or an attacker on the path might.
    /// </summary>
    public async Task InjectDataAsync(IPEndPoint peer, byte[] data)
    {
        foreach (Allocation allocation in _allocations.Values)
        {
            byte[] frame = new byte[data.Length + 64];
            StunMessageWriter writer = new(
                frame,
                StunMessageClass.Indication,
                StunMethod.Data,
                RandomNumberGenerator.GetBytes(12)
            );
            writer.AddXorAddress(StunAttributeType.XorPeerAddress, peer);
            writer.AddAttribute(StunAttributeType.Data, data);
            _ = await allocation.Reply(frame.AsMemory(0, writer.Length));
        }
    }

    /// <summary>Forgets every allocation, as a server that restarted or expired them would.</summary>
    public void ForgetAllocations()
    {
        foreach (object key in _allocations.Keys)
        {
            if (_allocations.TryRemove(key, out Allocation? allocation))
            {
                allocation.Relay.Dispose();
            }
        }
    }

    private sealed class Allocation(Socket relay, Func<ReadOnlyMemory<byte>, ValueTask<int>> reply)
    {
        public Socket Relay { get; } = relay;

        public Func<ReadOnlyMemory<byte>, ValueTask<int>> Reply { get; } = reply;

        public ConcurrentDictionary<IPAddress, bool> Permissions { get; } = new();

        public ConcurrentDictionary<ushort, IPEndPoint> Channels { get; } = new();

        public ConcurrentDictionary<IPEndPoint, ushort> ChannelsByPeer { get; } = new();
    }
}
