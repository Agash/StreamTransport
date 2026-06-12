using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Agash.StreamTransport.WebRtc.Ice;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// An in-memory ICE datagram network for deterministic tests: <see cref="IIceSocket"/>s route datagrams to
/// each other by endpoint with no real sockets or wall-clock timing, and a path can be "cut" to simulate a
/// link going dark (a cellular handover, a Wi-Fi drop). This is what makes ICE candidate switching / failover
/// testable - the whole point of abstracting the datagram layer.
/// </summary>
internal sealed class InMemoryIceNetwork
{
    private readonly ConcurrentDictionary<IPEndPoint, FakeSocket> _sockets = new();
    private readonly HashSet<IPAddress> _cut = [];
    private readonly Lock _gate = new();
    private int _nextPort = 50_000;
    private double _lossRate;
    private Random? _lossRng;

    /// <summary>A socket factory that offers <paramref name="addresses"/> as this agent's local interfaces.</summary>
    public IIceSocketFactory Factory(params IPAddress[] addresses) => new FakeFactory(this, addresses);

    /// <summary>Cut all traffic to and from <paramref name="address"/> (the path goes dark).</summary>
    public void Cut(IPAddress address)
    {
        lock (_gate)
        {
            _cut.Add(address);
        }
    }

    /// <summary>
    /// Drop a fraction (0..1) of datagrams at random to simulate a flaky link that is degraded but not cut - a
    /// weak cellular signal. Seeded for reproducibility. Unlike <see cref="Cut"/>, the path stays up, so consent
    /// freshness retries should keep the connection alive.
    /// </summary>
    public void SetLossRate(double rate, int seed = 12345)
    {
        lock (_gate)
        {
            _lossRate = rate;
            _lossRng = new Random(seed);
        }
    }

    private bool IsCut(IPAddress address)
    {
        lock (_gate)
        {
            return _cut.Contains(address);
        }
    }

    private bool ShouldDropRandomly()
    {
        lock (_gate)
        {
            return _lossRng is not null && _lossRng.NextDouble() < _lossRate;
        }
    }

    private void Deliver(IPEndPoint from, IPEndPoint to, byte[] data)
    {
        if (IsCut(from.Address) || IsCut(to.Address))
        {
            return; // dropped: this path is dark.
        }

        if (ShouldDropRandomly())
        {
            return; // dropped: the flaky link lost this datagram.
        }

        if (_sockets.TryGetValue(to, out FakeSocket? destination))
        {
            destination.Enqueue(from, data);
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

    private sealed class FakeFactory(InMemoryIceNetwork network, IPAddress[] addresses) : IIceSocketFactory
    {
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
        private readonly Channel<(IPEndPoint From, byte[] Data)> _rx =
            Channel.CreateUnbounded<(IPEndPoint, byte[])>(new UnboundedChannelOptions { SingleReader = true });

        public FakeSocket(InMemoryIceNetwork network, IPAddress address)
        {
            _network = network;
            LocalEndPoint = network.Bind(this, address);
        }

        public IPEndPoint LocalEndPoint { get; }

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, IPEndPoint destination, CancellationToken cancellationToken = default)
        {
            _network.Deliver(LocalEndPoint, destination, data.ToArray());
            return ValueTask.CompletedTask;
        }

        public async ValueTask<IceReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            (IPEndPoint from, byte[] data) = await _rx.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            data.CopyTo(buffer.Span);
            return new IceReceiveResult(data.Length, from);
        }

        public void Enqueue(IPEndPoint from, byte[] data) => _rx.Writer.TryWrite((from, data));

        public void Dispose() => _rx.Writer.TryComplete();
    }
}
