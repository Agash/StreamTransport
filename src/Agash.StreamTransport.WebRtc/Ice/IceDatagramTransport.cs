using System.Threading.Channels;
using Dtls.Core;

namespace Agash.StreamTransport.WebRtc.Ice;

// Carries DTLS over the ICE agent's selected pair. The ICE receive path demultiplexes by first byte
// and hands DTLS datagrams in here; they are copied, since the receive buffer is reused.
internal sealed class IceDatagramTransport(IceAgent agent) : IDatagramTransport
{
    // DTLS traffic after the handshake is a few alerts and retransmissions, so a small queue that drops
    // the oldest datagram is enough; losing one is no different from the network losing it.
    private readonly Channel<byte[]> _inbound = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        }
    );

    public void Deliver(ReadOnlySpan<byte> datagram) =>
        _inbound.Writer.TryWrite(datagram.ToArray());

    public void Complete() => _inbound.Writer.TryComplete();

    public async ValueTask<int> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        byte[] datagram = await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        int length = Math.Min(datagram.Length, buffer.Length);
        datagram.AsSpan(0, length).CopyTo(buffer.Span);
        return length;
    }

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> datagram,
        CancellationToken cancellationToken
    ) => agent.SendAsync(datagram, cancellationToken);
}
