using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Agash.StreamTransport.WebRtc.Ice;
using Agash.StreamTransport.WebRtc.Stun;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.WebRtc.Turn;

/// <summary>
/// A TURN allocation (RFC 8656): a UDP relay address on a TURN server, used by ICE as a local socket.
/// Sending to a peer goes through a channel once one is bound (four octets of overhead) and as a Send
/// indication until then; what peers send comes back unwrapped, with the peer as the source. The
/// allocation, its channels and their permissions are refreshed on the injected clock.
/// </summary>
/// <remarks>
/// After <see cref="AllocateAsync"/> returns, the ICE receive loop drives the connection through
/// <see cref="ReceiveAsync"/>: it returns peer data and completes the allocation's own transactions on
/// the way, so no second reader competes for the connection.
/// </remarks>
internal sealed partial class TurnAllocation : IIceSocket
{
    private const uint RequestedLifetime = 600;
    private const int MaxUdpTransmits = 5;
    private const ushort FirstChannel = 0x4000;
    private const ushort LastChannel = 0x4FFF;

    // Permissions last five minutes and a ChannelBind refreshes both its channel and its permission.
    private static readonly TimeSpan BindingRefresh = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan BindingRetry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaintenanceTick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InitialRto = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReliableTimeout = TimeSpan.FromSeconds(39.5);

    private readonly TurnServer _server;
    private readonly TurnConnection _connection;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly long _origin;
    private readonly Lock _gate = new();
    private readonly Dictionary<IPEndPoint, Peer> _peers = [];
    private readonly Dictionary<ushort, IPEndPoint> _channels = [];
    private readonly Dictionary<UInt128, TaskCompletionSource<byte[]>> _pending = [];
    private readonly CancellationTokenSource _lifetime = new();
    private ITimer? _maintenance;
    private byte[] _key = [];
    private byte[] _realm = [];
    private byte[] _nonce = [];
    private ushort _nextChannel = FirstChannel;
    private TimeSpan _expires;
    private int _maintaining;
    private int _disposed;

    private TurnAllocation(
        TurnServer server,
        TurnConnection connection,
        TimeProvider time,
        ILogger logger
    )
    {
        _server = server;
        _connection = connection;
        _time = time;
        _logger = logger;
        _origin = time.GetTimestamp();
        LocalEndPoint = new IPEndPoint(IPAddress.None, 0);
        MappedAddress = LocalEndPoint;
    }

    /// <summary>The relayed transport address peers send to.</summary>
    public IPEndPoint LocalEndPoint { get; private set; }

    /// <summary>The client's address as the server saw it, the relayed candidate's related address.</summary>
    public IPEndPoint MappedAddress { get; private set; }

    private TimeSpan Now => _time.GetElapsedTime(_origin);

    /// <summary>
    /// Connects to <paramref name="server"/> at <paramref name="endpoint"/> and allocates a relay of the
    /// endpoint's address family, answering the server's long-term credential challenge.
    /// </summary>
    /// <param name="server">The server and credentials.</param>
    /// <param name="endpoint">The server address to use.</param>
    /// <param name="time">The clock for retransmissions and refreshes.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">Cancels the allocation.</param>
    /// <returns>The allocation.</returns>
    /// <exception cref="TurnException">The server refused the allocation.</exception>
    public static async Task<TurnAllocation> AllocateAsync(
        TurnServer server,
        IPEndPoint endpoint,
        TimeProvider time,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        TurnConnection connection = await TurnConnection
            .ConnectAsync(server, endpoint, cancellationToken)
            .ConfigureAwait(false);
        var allocation = new TurnAllocation(server, connection, time, logger);
        try
        {
            await allocation.RequestAllocationAsync(endpoint.AddressFamily, cancellationToken).ConfigureAwait(false);
            allocation._maintenance = time.CreateTimer(
                static state => ((TurnAllocation)state!).Maintain(),
                allocation,
                MaintenanceTick,
                MaintenanceTick
            );
            return allocation;
        }
        catch
        {
            allocation.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> data,
        IPEndPoint destination,
        CancellationToken cancellationToken = default
    )
    {
        Peer peer;
        bool bind;
        lock (_gate)
        {
            if (!_peers.TryGetValue(destination, out peer!))
            {
                if (_nextChannel > LastChannel)
                {
                    // Every channel is taken; this peer is reached by Send indications alone.
                    peer = new Peer(destination, 0);
                }
                else
                {
                    peer = new Peer(destination, _nextChannel++);
                    _channels[peer.Channel] = destination;
                }

                _peers[destination] = peer;
            }

            bind = peer.Channel != 0 && !peer.Binding && Now >= peer.RetryAt && !peer.Bound;
            peer.Binding |= bind;
        }

        if (bind)
        {
            _ = BindAsync(peer, cancellationToken);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(data.Length + 64);
        try
        {
            int length = peer.Bound
                ? WriteChannelData(buffer, peer.Channel, data.Span)
                : WriteSendIndication(buffer, destination, data.Span);
            await _connection
                .SendAsync(buffer.AsMemory(0, length), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<IceReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            int length = await _connection
                .ReceiveAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (Unwrap(buffer.Span[..length], out int dataLength) is { } source)
            {
                return new IceReceiveResult(dataLength, source);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _maintenance?.Dispose();
        _lifetime.Cancel();
        try
        {
            // Releases the relay now instead of when its lifetime runs out.
            byte[] buffer = new byte[1024];
            int length = WriteRequest(buffer, StunMethod.Refresh, NewTransaction(), static (ref w) =>
                w.AddUInt32(StunAttributeType.Lifetime, 0)
            );
            _connection.SendFinal(buffer.AsSpan(0, length));
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            LogDeallocateFailed(exception, _server.Host);
        }

        lock (_gate)
        {
            foreach (TaskCompletionSource<byte[]> pending in _pending.Values)
            {
                _ = pending.TrySetCanceled();
            }

            _pending.Clear();
        }

        _connection.Dispose();
        _lifetime.Dispose();
    }

    // Allocate, answering the 401 challenge and a stale nonce once each. The ICE loop is not reading yet,
    // so each exchange reads its own response.
    private async Task RequestAllocationAsync(AddressFamily family, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            byte[] response = await ExchangeDirectAsync(
                    transaction => BuildAllocate(transaction, family),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (!StunMessageReader.TryParse(response, out StunMessageReader message))
            {
                throw new TurnException(0, "The TURN server sent a malformed response.");
            }

            if (message.Class == StunMessageClass.SuccessResponse)
            {
                if (
                    !VerifyIntegrity(message)
                    || !message.TryGetXorAddress(StunAttributeType.XorRelayedAddress, out IPEndPoint relayed)
                )
                {
                    throw new TurnException(0, "The TURN server's allocation response did not verify.");
                }

                LocalEndPoint = relayed;
                MappedAddress = message.TryGetXorMappedAddress(out IPEndPoint mapped) ? mapped : relayed;
                _expires = Now + Lifetime(message);
                LogAllocated(_server.Host, relayed, MappedAddress);
                return;
            }

            if (!HandleChallenge(message, out int code))
            {
                throw new TurnException(code, $"The TURN server refused the allocation ({code}).");
            }
        }

        throw new TurnException(401, "The TURN server rejected the credentials.");
    }

    // A 401 carries the realm and nonce to authenticate with, a 438 a fresh nonce. Either way the request
    // is sent again; any other error ends it.
    private bool HandleChallenge(StunMessageReader message, out int code)
    {
        _ = message.TryGetErrorCode(out code);
        if (
            code is not (401 or 438)
            || !message.TryFindAttribute(StunAttributeType.Nonce, out ReadOnlySpan<byte> nonce)
        )
        {
            return false;
        }

        lock (_gate)
        {
            _nonce = nonce.ToArray();
            if (message.TryFindAttribute(StunAttributeType.Realm, out ReadOnlySpan<byte> realm))
            {
                _realm = realm.ToArray();
                _key = LongTermKey(_server.Username, realm, _server.Credential);
            }
        }

        return _key.Length > 0;
    }

    // Sends a request and reads the connection until its response arrives, retransmitting on UDP.
    private async Task<byte[]> ExchangeDirectAsync(
        Func<UInt128, byte[]> build,
        CancellationToken cancellationToken
    )
    {
        UInt128 transaction = NewTransaction();
        byte[] request = build(transaction);
        byte[] buffer = new byte[2048];
        TimeSpan rto = _connection.IsReliable ? ReliableTimeout : InitialRto;
        for (int transmit = 0; transmit < (_connection.IsReliable ? 1 : MaxUdpTransmits); transmit++)
        {
            await _connection.SendAsync(request, cancellationToken).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(rto, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token,
                cancellationToken
            );
            try
            {
                while (true)
                {
                    int length = await _connection
                        .ReceiveAsync(buffer, linked.Token)
                        .ConfigureAwait(false);
                    if (IsResponseTo(buffer.AsSpan(0, length), transaction))
                    {
                        return buffer[..length];
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                rto *= 2;
            }
        }

        throw new TurnException(0, $"The TURN server {_server.Host} did not answer.");
    }

    // Sends an authenticated request whose response the ICE receive loop delivers, retrying once on a
    // stale nonce. Returns the success response, or null with the error logged.
    private async Task<bool> TransactAsync(
        StunMethod method,
        WriteAttributes attributes,
        CancellationToken cancellationToken
    )
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            UInt128 transaction = NewTransaction();
            byte[] buffer = new byte[1024];
            byte[] request = buffer[..WriteRequest(buffer, method, transaction, attributes)];
            var completion = new TaskCompletionSource<byte[]>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            lock (_gate)
            {
                _pending[transaction] = completion;
            }

            byte[]? response = null;
            try
            {
                TimeSpan rto = _connection.IsReliable ? ReliableTimeout : InitialRto;
                for (int transmit = 0; transmit < (_connection.IsReliable ? 1 : MaxUdpTransmits); transmit++)
                {
                    await _connection.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        response = await completion
                            .Task.WaitAsync(rto, _time, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                    catch (TimeoutException)
                    {
                        rto *= 2;
                    }
                }
            }
            finally
            {
                lock (_gate)
                {
                    _ = _pending.Remove(transaction);
                }
            }

            if (response is null)
            {
                LogRequestTimedOut(method, _server.Host);
                return false;
            }

            if (!StunMessageReader.TryParse(response, out StunMessageReader message))
            {
                return false;
            }

            if (message.Class == StunMessageClass.SuccessResponse)
            {
                if (!VerifyIntegrity(message))
                {
                    return false;
                }

                if (method == StunMethod.Refresh)
                {
                    _expires = Now + Lifetime(message);
                }

                return true;
            }

            if (!HandleChallenge(message, out int code))
            {
                LogRequestRefused(method, code, _server.Host);
                return false;
            }
        }

        return false;
    }

    // Binds the peer's channel, which also installs its permission. A failure is retried after a pause
    // on the next send; Send indications carry the traffic meanwhile.
    private async Task BindAsync(Peer peer, CancellationToken cancellationToken)
    {
        bool bound = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token
            );
            bound = await TransactAsync(
                    StunMethod.ChannelBind,
                    (ref w) =>
                    {
                        w.AddUInt32(StunAttributeType.ChannelNumber, (uint)peer.Channel << 16);
                        w.AddXorAddress(StunAttributeType.XorPeerAddress, peer.Endpoint);
                    },
                    linked.Token
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // Deliberately not logged: the allocation or the send that asked for the binding is closing.
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            LogBindFailed(exception, peer.Endpoint, _server.Host);
        }

        lock (_gate)
        {
            peer.Binding = false;
            if (bound)
            {
                peer.Bound = true;
                peer.BoundAt = Now;
            }
            else
            {
                peer.RetryAt = Now + BindingRetry;
            }
        }
    }

    // Refreshes the allocation before it expires and every binding before its permission does.
    private void Maintain()
    {
        if (Interlocked.Exchange(ref _maintaining, 1) != 0)
        {
            return;
        }

        _ = MaintainAsync();
    }

    private async Task MaintainAsync()
    {
        try
        {
            CancellationToken cancellationToken = _lifetime.Token;
            if (_expires - Now < TimeSpan.FromSeconds(RequestedLifetime / 4))
            {
                _ = await TransactAsync(
                        StunMethod.Refresh,
                        static (ref w) => w.AddUInt32(StunAttributeType.Lifetime, RequestedLifetime),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            List<Peer> due;
            lock (_gate)
            {
                due = [.. _peers.Values.Where(p => p.Bound && !p.Binding && Now - p.BoundAt >= BindingRefresh)];
                foreach (Peer peer in due)
                {
                    peer.Binding = true;
                }
            }

            foreach (Peer peer in due)
            {
                await BindAsync(peer, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // Deliberately not logged: the allocation is closing.
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            LogRefreshFailed(exception, _server.Host);
        }
        finally
        {
            Volatile.Write(ref _maintaining, 0);
        }
    }

    // Peer data comes out at the start of the buffer with the peer as its source; responses complete
    // their transactions and yield nothing.
    private IPEndPoint? Unwrap(Span<byte> message, out int dataLength)
    {
        dataLength = 0;
        if (message.Length >= 4 && (message[0] & 0xC0) == 0x40)
        {
            ushort channel = BinaryPrimitives.ReadUInt16BigEndian(message);
            int length = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
            IPEndPoint? peer;
            lock (_gate)
            {
                _ = _channels.TryGetValue(channel, out peer);
            }

            if (peer is null || 4 + length > message.Length)
            {
                return null;
            }

            message.Slice(4, length).CopyTo(message);
            dataLength = length;
            return peer;
        }

        if (!StunMessageReader.TryParse(message, out StunMessageReader stun))
        {
            return null;
        }

        if (stun.Class == StunMessageClass.Indication && stun.Method == StunMethod.Data)
        {
            if (
                !stun.TryGetXorAddress(StunAttributeType.XorPeerAddress, out IPEndPoint peer)
                || !stun.TryFindAttribute(StunAttributeType.Data, out ReadOnlySpan<byte> data)
            )
            {
                return null;
            }

            dataLength = data.Length;
            data.CopyTo(message);
            return peer;
        }

        if (stun.Class is StunMessageClass.SuccessResponse or StunMessageClass.ErrorResponse)
        {
            TaskCompletionSource<byte[]>? pending;
            lock (_gate)
            {
                _ = _pending.TryGetValue(ReadTransaction(stun.TransactionId), out pending);
            }

            _ = pending?.TrySetResult(stun.Raw.ToArray());
        }

        return null;
    }

    private bool VerifyIntegrity(StunMessageReader message) =>
        _key.Length > 0 && message.VerifyMessageIntegrity(_key);

    private byte[] BuildAllocate(UInt128 transaction, AddressFamily family)
    {
        byte[] buffer = new byte[1024];
        return buffer[..WriteRequest(
            buffer,
            StunMethod.Allocate,
            transaction,
            (ref w) =>
            {
                // UDP relaying (protocol 17), of the family the client reached the server with.
                w.AddUInt32(StunAttributeType.RequestedTransport, 17u << 24);
                w.AddUInt32(
                    StunAttributeType.RequestedAddressFamily,
                    family == AddressFamily.InterNetworkV6 ? 0x02u << 24 : 0x01u << 24
                );
                w.AddUInt32(StunAttributeType.Lifetime, RequestedLifetime);
            }
        )];
    }

    // Writes a request with the long-term credential attributes once the server has challenged.
    private int WriteRequest(
        Span<byte> buffer,
        StunMethod method,
        UInt128 transaction,
        WriteAttributes attributes
    )
    {
        Span<byte> id = stackalloc byte[StunHeader.TransactionIdLength];
        WriteTransaction(transaction, id);
        StunMessageWriter writer = new(buffer, StunMessageClass.Request, method, id);
        attributes(ref writer);
        lock (_gate)
        {
            if (_key.Length > 0)
            {
                writer.AddAttribute(StunAttributeType.Username, Encoding.UTF8.GetBytes(_server.Username));
                writer.AddAttribute(StunAttributeType.Realm, _realm);
                writer.AddAttribute(StunAttributeType.Nonce, _nonce);
                writer.AddMessageIntegrity(_key);
            }
        }

        writer.AddFingerprint();
        return writer.Length;
    }

    private static int WriteSendIndication(Span<byte> buffer, IPEndPoint peer, ReadOnlySpan<byte> data)
    {
        Span<byte> id = stackalloc byte[StunHeader.TransactionIdLength];
        RandomNumberGenerator.Fill(id);
        StunMessageWriter writer = new(buffer, StunMessageClass.Indication, StunMethod.Send, id);
        writer.AddXorAddress(StunAttributeType.XorPeerAddress, peer);
        writer.AddAttribute(StunAttributeType.Data, data);
        return writer.Length;
    }

    private static int WriteChannelData(Span<byte> buffer, ushort channel, ReadOnlySpan<byte> data)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buffer, channel);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], (ushort)data.Length);
        data.CopyTo(buffer[4..]);
        return 4 + data.Length;
    }

    private static bool IsResponseTo(ReadOnlySpan<byte> message, UInt128 transaction) =>
        StunMessageReader.TryParse(message, out StunMessageReader stun)
        && stun.Class is StunMessageClass.SuccessResponse or StunMessageClass.ErrorResponse
        && ReadTransaction(stun.TransactionId) == transaction;

    private static TimeSpan Lifetime(StunMessageReader message) =>
        TimeSpan.FromSeconds(
            message.TryGetUInt32(StunAttributeType.Lifetime, out uint seconds) ? seconds : RequestedLifetime
        );

    // The long-term credential key: MD5(username ":" realm ":" password) (RFC 8489 section 9.2.2).
#pragma warning disable CA5351 // The STUN long-term credential mechanism defines the key as MD5.
    private static byte[] LongTermKey(string username, ReadOnlySpan<byte> realm, string password) =>
        MD5.HashData([.. Encoding.UTF8.GetBytes(username), (byte)':', .. realm, (byte)':', .. Encoding.UTF8.GetBytes(password)]);
#pragma warning restore CA5351

    private static UInt128 NewTransaction()
    {
        Span<byte> id = stackalloc byte[StunHeader.TransactionIdLength];
        RandomNumberGenerator.Fill(id);
        return ReadTransaction(id);
    }

    private static UInt128 ReadTransaction(ReadOnlySpan<byte> id) =>
        new(BinaryPrimitives.ReadUInt32BigEndian(id), BinaryPrimitives.ReadUInt64BigEndian(id[4..]));

    private static void WriteTransaction(UInt128 transaction, Span<byte> id)
    {
        BinaryPrimitives.WriteUInt32BigEndian(id, (uint)(transaction >> 64));
        BinaryPrimitives.WriteUInt64BigEndian(id[4..], (ulong)transaction);
    }

    [LoggerMessage(
        EventId = 1150,
        Level = LogLevel.Information,
        Message = "TURN allocation on {Server}: relayed {Relayed}, mapped {Mapped}"
    )]
    private partial void LogAllocated(string server, IPEndPoint relayed, IPEndPoint mapped);

    [LoggerMessage(EventId = 1151, Level = LogLevel.Warning, Message = "TURN {Method} to {Server} timed out")]
    private partial void LogRequestTimedOut(StunMethod method, string server);

    [LoggerMessage(EventId = 1152, Level = LogLevel.Warning, Message = "TURN {Method} refused by {Server} with {Code}")]
    private partial void LogRequestRefused(StunMethod method, int code, string server);

    [LoggerMessage(EventId = 1153, Level = LogLevel.Warning, Message = "TURN channel bind for {Peer} on {Server} failed")]
    private partial void LogBindFailed(Exception exception, IPEndPoint peer, string server);

    [LoggerMessage(EventId = 1154, Level = LogLevel.Warning, Message = "TURN refresh on {Server} failed")]
    private partial void LogRefreshFailed(Exception exception, string server);

    [LoggerMessage(EventId = 1155, Level = LogLevel.Debug, Message = "TURN deallocation on {Server} was not sent")]
    private partial void LogDeallocateFailed(Exception exception, string server);

    private delegate void WriteAttributes(ref StunMessageWriter writer);

    private sealed class Peer(IPEndPoint endpoint, ushort channel)
    {
        public IPEndPoint Endpoint { get; } = endpoint;

        public ushort Channel { get; } = channel;

        public bool Bound { get; set; }

        public bool Binding { get; set; }

        public TimeSpan BoundAt { get; set; }

        public TimeSpan RetryAt { get; set; }
    }
}

/// <summary>A TURN server refused or did not answer a request.</summary>
public sealed class TurnException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TurnException() { }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public TurnException(string message)
        : base(message) { }

    /// <summary>Creates the exception with a message and its cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public TurnException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates the exception for a STUN error code.</summary>
    /// <param name="code">The STUN error code, or 0 when the server sent none.</param>
    /// <param name="message">What went wrong.</param>
    public TurnException(int code, string message)
        : base(message) => Code = code;

    /// <summary>The STUN error code (RFC 8489 section 14.8), or 0 when the server sent none.</summary>
    public int Code { get; }
}
