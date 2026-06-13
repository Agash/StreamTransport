using System.Collections.Concurrent;
using Org.BouncyCastle.Tls;

namespace Agash.StreamTransport.WebRtc.Dtls;

/// <summary>
/// Bridges BouncyCastle's blocking <see cref="DatagramTransport"/> to the stack's record-based I/O:
/// outbound records go to the <see cref="DtlsRecordSender"/> (the ICE path); inbound records arrive via
/// <see cref="Enqueue"/> and are handed to the blocking handshake thread through a bounded queue.
/// </summary>
internal sealed class DtlsBridgeTransport(DtlsRecordSender send) : DatagramTransport
{
    // Cap handshake records to the IPv6 guaranteed minimum path MTU (1280 - 40 IPv6 - 8 UDP = 1232 usable);
    // 1200 matches libwebrtc. Routers do not fragment IPv6, so an oversized record is silently dropped.
    private const int SendMtu = 1200;
    private const int ReceiveMtu = 1500;

    private readonly BlockingCollection<byte[]> _inbound = new(new ConcurrentQueue<byte[]>());

    public void Enqueue(ReadOnlyMemory<byte> record) => _inbound.Add(record.ToArray());

    public int GetReceiveLimit() => ReceiveMtu;

    public int GetSendLimit() => SendMtu;

    public int Receive(byte[] buf, int off, int len, int waitMillis)
    {
        if (_inbound.TryTake(out byte[]? record, NormalizeWait(waitMillis)))
        {
            int n = Math.Min(record.Length, len);
            Buffer.BlockCopy(record, 0, buf, off, n);
            return n;
        }

        return -1; // timeout - BouncyCastle retransmits the current flight.
    }

    public int Receive(Span<byte> buffer, int waitMillis)
    {
        if (_inbound.TryTake(out byte[]? record, NormalizeWait(waitMillis)))
        {
            int n = Math.Min(record.Length, buffer.Length);
            record.AsSpan(0, n).CopyTo(buffer);
            return n;
        }

        return -1;
    }

    // BouncyCastle's DatagramReceiver contract: waitMillis == 0 means an infinite wait. BlockingCollection
    // treats 0 as "don't block", the opposite, so map it to Timeout.Infinite (-1).
    private static int NormalizeWait(int waitMillis) => waitMillis == 0 ? Timeout.Infinite : waitMillis;

    public void Send(byte[] buf, int off, int len) => send(buf.AsMemory(off, len).ToArray());

    public void Send(ReadOnlySpan<byte> buffer) => send(buffer.ToArray());

    public void Close() => _inbound.Dispose();
}
