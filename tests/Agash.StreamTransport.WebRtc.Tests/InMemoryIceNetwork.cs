using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.WebRtc.Ice;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// An in-memory datagram network: <see cref="IIceSocket"/>s that route datagrams to each other by
/// endpoint, so an <see cref="IceAgent"/> runs without real sockets.
/// </summary>
internal sealed class InMemoryIceNetwork
{
    private readonly ConcurrentDictionary<IPEndPoint, FakeSocket> _sockets = new();
    private readonly Lock _gate = new();
    private int _nextPort = 50_000;

    /// <summary>A socket factory that offers <paramref name="addresses"/> as this agent's local interfaces.</summary>
    public IIceSocketFactory Factory(params IPAddress[] addresses) =>
        new FakeFactory(this, addresses);

    /// <summary>The endpoints bound on an address so far.</summary>
    public IReadOnlyList<IPEndPoint> BoundOn(IPAddress address) =>
        [.. _sockets.Keys.Where(endpoint => endpoint.Address.Equals(address))];

    /// <summary>Decides which datagrams the network loses: true drops one.</summary>
    public Func<IPEndPoint, IPEndPoint, byte[], bool>? Drop { get; set; }

    /// <summary>
    /// What the network does to a datagram's ECN codepoint on the way, given the datagram: a middlebox that
    /// clears or rewrites it, or a test that records it. The codepoint arrives as sent when null.
    /// </summary>
    public Func<byte[], EcnCodepoint, EcnCodepoint>? Remark { get; set; }

    /// <summary>Puts a datagram on a socket as if it arrived from <paramref name="from"/>.</summary>
    public void Inject(IPEndPoint from, IPEndPoint to, byte[] data) =>
        Deliver(from, to, data, EcnCodepoint.NotEct);

    private void Deliver(IPEndPoint from, IPEndPoint to, byte[] data, EcnCodepoint ecn)
    {
        if (Drop?.Invoke(from, to, data) == true)
        {
            return;
        }

        if (_sockets.TryGetValue(to, out FakeSocket? destination))
        {
            destination.Enqueue(from, data, Remark?.Invoke(data, ecn) ?? ecn);
        }
    }

    private IPEndPoint Bind(FakeSocket socket, IPAddress address)
    {
        int port;
        lock (_gate)
        {
            port = _nextPort++;
        }

        var endpoint = new IPEndPoint(address, port);
        _sockets[endpoint] = socket;
        return endpoint;
    }

    private sealed class FakeFactory(InMemoryIceNetwork network, IPAddress[] addresses)
        : IIceSocketFactory
    {
        public EcnSupport Ecn => EcnSupport.SetRead;

        public IEnumerable<IPAddress> GetLocalAddresses(bool includeLoopback) => addresses;

        public bool TryBind(IPAddress address, out IIceSocket socket)
        {
            socket = new FakeSocket(network, address);
            return true;
        }
    }

    private sealed class FakeSocket : IIceSocket
    {
        private readonly InMemoryIceNetwork _network;
        private readonly Channel<(IPEndPoint From, byte[] Data, EcnCodepoint Ecn)> _rx =
            Channel.CreateUnbounded<(IPEndPoint, byte[], EcnCodepoint)>(
                new UnboundedChannelOptions { SingleReader = true }
            );

        public FakeSocket(InMemoryIceNetwork network, IPAddress address)
        {
            _network = network;
            LocalEndPoint = network.Bind(this, address);
        }

        public IPEndPoint LocalEndPoint { get; }

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> data,
            IPEndPoint destination,
            EcnCodepoint ecn,
            CancellationToken cancellationToken = default
        )
        {
            _network.Deliver(LocalEndPoint, destination, data.ToArray(), ecn);
            return ValueTask.CompletedTask;
        }

        public async ValueTask<IceReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken
        )
        {
            (IPEndPoint from, byte[] data, EcnCodepoint ecn) = await _rx
                .Reader.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            data.CopyTo(buffer.Span);
            return new IceReceiveResult(data.Length, from, (byte)ecn);
        }

        public void Enqueue(IPEndPoint from, byte[] data, EcnCodepoint ecn) =>
            _rx.Writer.TryWrite((from, data, ecn));

        public void Dispose() => _rx.Writer.TryComplete();
    }
}
