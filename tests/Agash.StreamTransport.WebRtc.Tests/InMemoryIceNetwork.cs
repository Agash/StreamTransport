using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
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

    private void Deliver(IPEndPoint from, IPEndPoint to, byte[] data)
    {
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

    private sealed class FakeFactory(InMemoryIceNetwork network, IPAddress[] addresses)
        : IIceSocketFactory
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
        private readonly Channel<(IPEndPoint From, byte[] Data)> _rx = Channel.CreateUnbounded<(
            IPEndPoint,
            byte[]
        )>(new UnboundedChannelOptions { SingleReader = true });

        public FakeSocket(InMemoryIceNetwork network, IPAddress address)
        {
            _network = network;
            LocalEndPoint = network.Bind(this, address);
        }

        public IPEndPoint LocalEndPoint { get; }

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> data,
            IPEndPoint destination,
            CancellationToken cancellationToken = default
        )
        {
            _network.Deliver(LocalEndPoint, destination, data.ToArray());
            return ValueTask.CompletedTask;
        }

        public async ValueTask<IceReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken
        )
        {
            (IPEndPoint from, byte[] data) = await _rx
                .Reader.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            data.CopyTo(buffer.Span);
            return new IceReceiveResult(data.Length, from);
        }

        public void Enqueue(IPEndPoint from, byte[] data) => _rx.Writer.TryWrite((from, data));

        public void Dispose() => _rx.Writer.TryComplete();
    }
}
