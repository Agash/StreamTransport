using System.Buffers;
using System.Threading.Channels;
using Dtls.Core;

namespace Agash.StreamTransport.WebRtc.Ice;

// Carries DTLS over the ICE agent's selected pair. The ICE receive path demultiplexes by first byte
// and hands DTLS datagrams in here; they are copied into pooled buffers, since the receive buffer is
// reused, and each goes back to the pool once DTLS has read it or the queue dropped it.
internal sealed class IceDatagramTransport(IceAgent agent) : IDatagramTransport
{
    // DTLS traffic after the handshake is a few alerts and retransmissions, so a small queue that drops
    // the oldest datagram is enough; losing one is no different from the network losing it.
    private readonly Channel<Datagram> _inbound = Channel.CreateBounded<Datagram>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        },
        static dropped => ArrayPool<byte>.Shared.Return(dropped.Buffer)
    );

    public void Deliver(ReadOnlySpan<byte> datagram)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(datagram.Length);
        datagram.CopyTo(buffer);
        if (!_inbound.Writer.TryWrite(new Datagram(buffer, datagram.Length)))
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Complete()
    {
        _ = _inbound.Writer.TryComplete();
        while (_inbound.Reader.TryRead(out Datagram left))
        {
            ArrayPool<byte>.Shared.Return(left.Buffer);
        }
    }

    public async ValueTask<int> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        Datagram datagram = await _inbound
            .Reader.ReadAsync(cancellationToken)
            .ConfigureAwait(false);
        int length = Math.Min(datagram.Length, buffer.Length);
        datagram.Buffer.AsSpan(0, length).CopyTo(buffer.Span);
        ArrayPool<byte>.Shared.Return(datagram.Buffer);
        return length;
    }

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> datagram,
        CancellationToken cancellationToken
    ) => agent.SendAsync(datagram, cancellationToken: cancellationToken);

    private readonly record struct Datagram(byte[] Buffer, int Length);
}
