using System.Buffers.Binary;
using System.Collections.Concurrent;
using Dtls.Core;

namespace Agash.StreamTransport.WebRtc.Srtp;

/// <summary>
/// An SRTP session derived from the DTLS-SRTP keying material (RFC 5764). It derives the per-direction
/// session keys (RFC 3711 KDF), keeps the 48-bit packet index per SSRC, rejects replays, and protects and
/// unprotects RTP and RTCP with the negotiated profile: AES-GCM (RFC 7714) or AES-CM with HMAC-SHA1
/// (RFC 3711). One instance per DTLS association; the DTLS role decides which master key protects the
/// outbound direction.
/// </summary>
/// <remarks>
/// Keys are never reused across associations: a new DTLS handshake exports new keying material and
/// builds a new session. Within a session an outbound index is used once; protecting an index that was
/// already used throws, because a repeated AES-GCM nonce exposes the authentication key.
/// </remarks>
public sealed class SrtpSession : IDisposable
{
    /// <summary>The receive replay window in packets per SSRC (libwebrtc uses the same width).</summary>
    public const int ReplayWindowSize = 1024;

    private readonly ISrtpTransform _send;
    private readonly ISrtpTransform _receive;
    private readonly ConcurrentDictionary<uint, SendIndex> _sendIndex = new();
    private readonly ConcurrentDictionary<uint, ReceiveIndex> _receiveIndex = new();
    private readonly ConcurrentDictionary<uint, ReplayWindow> _receiveRtcp = new();

    // RFC 3711 section 3.4: the first SRTCP packet carries index 0.
    private int _srtcpSendIndex = -1;

    // The profiles this session implements, most preferred first. AES-GCM authenticates and encrypts in
    // one pass with a 16-octet tag; 128-bit keys come first because every peer that offers GCM offers
    // them. AES-CM with HMAC-SHA1-80 is the fallback for peers that offer no GCM profile.
    internal static readonly SrtpProtectionProfile[] Profiles =
    [
        SrtpProtectionProfile.AeadAes128Gcm,
        SrtpProtectionProfile.AeadAes256Gcm,
        SrtpProtectionProfile.Aes128CmHmacSha180,
    ];

    /// <summary>
    /// Builds the session from exported keying material. <paramref name="isDtlsClient"/> selects which
    /// master key/salt protects the outbound direction: the DTLS client sends with the client write keys,
    /// the server with the server write keys (RFC 5764 section 4.2).
    /// </summary>
    public SrtpSession(SrtpKeyingMaterial keying, bool isDtlsClient)
    {
        ArgumentNullException.ThrowIfNull(keying);
        ISrtpTransform client = Transform(
            keying.Profile,
            keying.ClientMasterKey.Span,
            keying.ClientMasterSalt.Span
        );
        ISrtpTransform server = Transform(
            keying.Profile,
            keying.ServerMasterKey.Span,
            keying.ServerMasterSalt.Span
        );
        (_send, _receive) = isDtlsClient ? (client, server) : (server, client);
    }

    /// <summary>The most bytes protection adds to an RTP packet under any profile, for sizing buffers.</summary>
    public static int MaxProtectionOverhead => SrtpGcmTransform.TagLength;

    /// <summary>The most bytes protection adds to an RTCP packet under any profile, for sizing buffers.</summary>
    public static int MaxRtcpProtectionOverhead => SrtpGcmTransform.RtcpTrailerLength;

    /// <summary>The bytes protection adds to an RTP packet under this session's profile.</summary>
    public int ProtectionOverhead => _send.RtpOverhead;

    /// <summary>
    /// Whether sent packets encrypt their header extensions too (Cryptex, RFC 9335): set once the peer
    /// said it receives them (<c>a=cryptex</c>). Received packets are read either way.
    /// </summary>
    public bool SendCryptex { get; set; }

    /// <summary>
    /// Protects an RTP packet in place, returning the protected length. <paramref name="packet"/> needs
    /// <see cref="ProtectionOverhead"/> spare octets.
    /// </summary>
    /// <exception cref="InvalidOperationException">The packet's index was already protected.</exception>
    public int ProtectRtp(Span<byte> packet, int length) =>
        TryProtectRtp(packet, length, out int protectedLength)
            ? protectedLength
            : throw new InvalidOperationException(
                "The packet's RTP sequence does not follow the last one protected on its SSRC; protecting it would reuse an SRTP index."
            );

    /// <summary>
    /// Protects an RTP packet in place unless its index does not follow the last one protected on its SSRC,
    /// which would reuse a nonce: a packet the send path reordered is dropped, not sent.
    /// </summary>
    /// <param name="packet">The packet, with <see cref="ProtectionOverhead"/> spare octets.</param>
    /// <param name="length">Its length.</param>
    /// <param name="protectedLength">The protected length.</param>
    /// <returns>Whether it was protected.</returns>
    public bool TryProtectRtp(Span<byte> packet, int length, out int protectedLength)
    {
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(8, 4));
        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        if (
            !_sendIndex.GetOrAdd(ssrc, static _ => new SendIndex()).TryNext(ssrc, seq, out uint roc)
        )
        {
            protectedLength = 0;
            return false;
        }

        protectedLength = _send.ProtectRtp(roc, packet, length, SendCryptex);
        return true;
    }

    /// <summary>
    /// Authenticates and decrypts a protected RTP packet in place, writing the recovered plaintext length
    /// to <paramref name="plaintextLength"/>. Returns <see langword="false"/> for a replayed packet or an
    /// authentication failure.
    /// </summary>
    public bool UnprotectRtp(Span<byte> packet, int length, out int plaintextLength)
    {
        plaintextLength = 0;
        if (length < 12)
        {
            return false;
        }

        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(8, 4));
        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        // A stream's state is kept only once a packet of it authenticates, as libsrtp does, so forged
        // packets cannot grow the table.
        bool known = _receiveIndex.TryGetValue(ssrc, out ReceiveIndex? state);
        state ??= new ReceiveIndex();
        lock (state)
        {
            ulong index = state.Estimate(seq);
            if (
                state.Window.IsReplay(index)
                || !_receive.UnprotectRtp((uint)(index >> 16), packet, length, out plaintextLength)
            )
            {
                return false;
            }

            state.Commit(index);
        }

        if (!known)
        {
            _ = _receiveIndex.TryAdd(ssrc, state);
        }

        return true;
    }

    // The streams whose packets have authenticated, for tests.
    internal int KnownReceiveStreams => _receiveIndex.Count + _receiveRtcp.Count;

    /// <summary>Protects an RTCP packet in place (SRTCP), assigning the next outbound SRTCP index.</summary>
    /// <exception cref="InvalidOperationException">The 31-bit SRTCP index is exhausted.</exception>
    public int ProtectRtcp(Span<byte> packet, int length)
    {
        int index = Interlocked.Increment(ref _srtcpSendIndex);
        if (index is < 0 or > 0x7FFF_FFFF)
        {
            throw new InvalidOperationException(
                "The SRTCP index is exhausted; the session must be re-keyed."
            );
        }

        return _send.ProtectRtcp((uint)index, packet, length);
    }

    /// <summary>
    /// Authenticates and decrypts an SRTCP packet in place, writing the recovered RTCP length. Returns
    /// <see langword="false"/> for a replayed packet or an authentication failure.
    /// </summary>
    public bool UnprotectRtcp(Span<byte> packet, int length, out int plaintextLength)
    {
        plaintextLength = 0;
        if (length < 8 + _receive.RtcpOverhead)
        {
            return false;
        }

        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4, 4));
        ulong index =
            BinaryPrimitives.ReadUInt32BigEndian(packet[(length - _receive.RtcpOverhead)..])
            & 0x7FFF_FFFFu;
        bool known = _receiveRtcp.TryGetValue(ssrc, out ReplayWindow? window);
        window ??= new ReplayWindow();
        lock (window)
        {
            if (
                window.IsReplay(index)
                || !_receive.UnprotectRtcp(packet, length, out plaintextLength)
            )
            {
                return false;
            }

            window.Accept(index);
        }

        if (!known)
        {
            _ = _receiveRtcp.TryAdd(ssrc, window);
        }

        return true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        (_send as IDisposable)?.Dispose();
        (_receive as IDisposable)?.Dispose();
    }

    private static ISrtpTransform Transform(
        SrtpProtectionProfile profile,
        ReadOnlySpan<byte> masterKey,
        ReadOnlySpan<byte> masterSalt
    ) =>
        profile switch
        {
            SrtpProtectionProfile.AeadAes128Gcm or SrtpProtectionProfile.AeadAes256Gcm =>
                SrtpGcmTransform.FromMaster(masterKey, masterSalt),
            SrtpProtectionProfile.Aes128CmHmacSha180 => new SrtpCmTransform(masterKey, masterSalt),
            _ => throw new NotSupportedException($"The SRTP profile {profile} is not implemented."),
        };

    // The outbound 48-bit index for one SSRC: strictly increasing, the rollover counter advancing when the
    // sequence number wraps. A repeat or a step back would reuse a nonce, so it is refused.
    private sealed class SendIndex
    {
        private const ulong MaxIndex = (1UL << 48) - 1;

        private readonly Lock _gate = new();
        private ulong _last;
        private bool _seen;

        public bool TryNext(uint ssrc, ushort seq, out uint rolloverCounter)
        {
            rolloverCounter = 0;
            lock (_gate)
            {
                ulong roc = _last >> 16;
                if (_seen && seq <= (ushort)_last)
                {
                    roc++;
                }

                ulong index = (roc << 16) | seq;
                if (_seen && (index <= _last || index - _last > 0x8000))
                {
                    return false;
                }

                if (index > MaxIndex)
                {
                    throw new InvalidOperationException(
                        $"The SRTP index for SSRC {ssrc} is exhausted; the session must be re-keyed."
                    );
                }

                _last = index;
                _seen = true;
                rolloverCounter = (uint)roc;
                return true;
            }
        }
    }

    // The inbound packet index for one SSRC (RFC 3711 section 3.3.1). The estimate does not move the state;
    // only an authenticated packet commits, so a forged packet cannot advance the rollover counter.
    private sealed class ReceiveIndex
    {
        private ulong _highest;
        private bool _seen;

        public ReplayWindow Window { get; } = new();

        public ulong Estimate(ushort seq)
        {
            if (!_seen)
            {
                return seq;
            }

            ulong roc = _highest >> 16;
            ushort highestSeq = (ushort)_highest;
            ulong v = roc;
            if (highestSeq < 0x8000)
            {
                if (seq - highestSeq > 0x8000 && roc > 0)
                {
                    v = roc - 1;
                }
            }
            else if (highestSeq - 0x8000 > seq)
            {
                v = roc + 1;
            }

            return (v << 16) | seq;
        }

        public void Commit(ulong index)
        {
            Window.Accept(index);
            if (!_seen || index > _highest)
            {
                _highest = index;
                _seen = true;
            }
        }
    }

    // A sliding replay window over packet indices (RFC 3711 section 3.3.2): an index older than the window or
    // already accepted is a replay. Callers hold the owner's lock.
    private sealed class ReplayWindow
    {
        private readonly ulong[] _bits = new ulong[ReplayWindowSize / 64];
        private ulong _highest;
        private bool _seen;

        public bool IsReplay(ulong index)
        {
            if (!_seen || index > _highest)
            {
                return false;
            }

            ulong age = _highest - index;
            return age >= ReplayWindowSize || (_bits[Slot(index)] & Bit(index)) != 0;
        }

        public void Accept(ulong index)
        {
            if (_seen && index <= _highest)
            {
                _bits[Slot(index)] |= Bit(index);
                return;
            }

            ulong advance = _seen ? index - _highest : ReplayWindowSize;
            if (advance >= ReplayWindowSize)
            {
                Array.Clear(_bits);
            }
            else
            {
                for (ulong i = _highest + 1; i < index; i++)
                {
                    _bits[Slot(i)] &= ~Bit(i);
                }
            }

            _bits[Slot(index)] |= Bit(index);
            _highest = index;
            _seen = true;
        }

        // The window is a ring over the low bits of the index.
        private static int Slot(ulong index) => (int)(index % ReplayWindowSize / 64);

        private static ulong Bit(ulong index) => 1UL << (int)(index % 64);
    }
}
