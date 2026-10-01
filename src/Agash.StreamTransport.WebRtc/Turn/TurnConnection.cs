using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace Agash.StreamTransport.WebRtc.Turn;

/// <summary>
/// The client's link to a TURN server: one STUN message or ChannelData frame per send and per receive.
/// Over UDP that is a datagram; over TCP and TLS the frames are delimited by their own length fields
/// (RFC 8656 section 12.5), and ChannelData is padded to four octets.
/// </summary>
internal abstract class TurnConnection : IDisposable
{
    /// <summary>Whether the link is a byte stream (TCP or TLS), so requests are not retransmitted.</summary>
    public abstract bool IsReliable { get; }

    /// <summary>Connects to <paramref name="server"/> at one of its resolved addresses.</summary>
    /// <param name="server">The server.</param>
    /// <param name="endpoint">The resolved address to connect to.</param>
    /// <param name="cancellationToken">Cancels connecting.</param>
    /// <returns>The connection.</returns>
    public static async Task<TurnConnection> ConnectAsync(
        TurnServer server,
        IPEndPoint endpoint,
        CancellationToken cancellationToken
    )
    {
        if (server.Transport == TurnTransport.Udp)
        {
            var udp = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                await udp.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                return new UdpTurnConnection(udp);
            }
            catch
            {
                udp.Dispose();
                throw;
            }
        }

        var tcp = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };
        try
        {
            await tcp.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            Stream stream = new NetworkStream(tcp, ownsSocket: true);
            if (server.Transport == TurnTransport.Tls)
            {
                var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                try
                {
                    await tls.AuthenticateAsClientAsync(
                            new SslClientAuthenticationOptions
                            {
                                TargetHost = server.Host,
                                RemoteCertificateValidationCallback =
                                    server.CertificateValidation,
                            },
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
                catch
                {
                    await tls.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                stream = tls;
            }

            return new StreamTurnConnection(stream);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Sends one message or frame.</summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the message is handed to the network.</returns>
    public abstract ValueTask SendAsync(
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken
    );

    /// <summary>Sends one message without waiting, for the deallocation on shutdown.</summary>
    /// <param name="message">The message.</param>
    public abstract void SendFinal(ReadOnlySpan<byte> message);

    /// <summary>Receives the next message or frame into <paramref name="buffer"/>.</summary>
    /// <param name="buffer">Where to put it.</param>
    /// <param name="cancellationToken">Cancels the receive.</param>
    /// <returns>Its length, or 0 for a frame too large for the buffer, which is skipped.</returns>
    public abstract ValueTask<int> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken
    );

    /// <inheritdoc/>
    public abstract void Dispose();

    private sealed class UdpTurnConnection(Socket socket) : TurnConnection
    {
        public override bool IsReliable => false;

        public override async ValueTask SendAsync(
            ReadOnlyMemory<byte> message,
            CancellationToken cancellationToken
        ) => _ = await socket.SendAsync(message, cancellationToken).ConfigureAwait(false);

        public override void SendFinal(ReadOnlySpan<byte> message) => _ = socket.Send(message);

        public override ValueTask<int> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken
        ) => socket.ReceiveAsync(buffer, cancellationToken);

        public override void Dispose() => socket.Dispose();
    }

    private sealed class StreamTurnConnection(Stream stream) : TurnConnection
    {
        private readonly SemaphoreSlim _writer = new(1, 1);
        private readonly byte[] _frame = new byte[4 + 65535 + 3];

        public override bool IsReliable => true;

        public override async ValueTask SendAsync(
            ReadOnlyMemory<byte> message,
            CancellationToken cancellationToken
        )
        {
            await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                int pad = Padding(message.Span);
                if (pad > 0)
                {
                    await stream
                        .WriteAsync(new byte[pad], cancellationToken)
                        .ConfigureAwait(false);
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _ = _writer.Release();
            }
        }

        public override void SendFinal(ReadOnlySpan<byte> message)
        {
            if (!_writer.Wait(0))
            {
                return;
            }

            try
            {
                stream.Write(message);
                stream.Write(stackalloc byte[Padding(message)]);
                stream.Flush();
            }
            finally
            {
                _ = _writer.Release();
            }
        }

        public override async ValueTask<int> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken
        )
        {
            await stream
                .ReadExactlyAsync(_frame.AsMemory(0, 4), cancellationToken)
                .ConfigureAwait(false);
            int length = FrameLength(_frame);
            await stream
                .ReadExactlyAsync(_frame.AsMemory(4, length - 4), cancellationToken)
                .ConfigureAwait(false);

            // The padding after a ChannelData frame is not part of it.
            int content = (_frame[0] & 0xC0) == 0x40
                ? 4 + BinaryPrimitives.ReadUInt16BigEndian(_frame.AsSpan(2))
                : length;
            if (content > buffer.Length)
            {
                return 0;
            }

            _frame.AsSpan(0, content).CopyTo(buffer.Span);
            return content;
        }

        public override void Dispose()
        {
            stream.Dispose();
            _writer.Dispose();
        }

        // A STUN message is its 20-octet header and body; a ChannelData frame is its 4-octet header and
        // data, padded to four octets on a stream. Anything else means the stream lost its framing.
        private static int FrameLength(ReadOnlySpan<byte> header)
        {
            int declared = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
            return (header[0] & 0xC0) switch
            {
                0x00 => 20 + declared,
                0x40 => 4 + ((declared + 3) & ~3),
                _ => throw new IOException("The TURN stream carried a frame that is neither STUN nor ChannelData."),
            };
        }

        private static int Padding(ReadOnlySpan<byte> message) =>
            (message[0] & 0xC0) == 0x40 ? (4 - (message.Length & 3)) & 3 : 0;
    }
}
