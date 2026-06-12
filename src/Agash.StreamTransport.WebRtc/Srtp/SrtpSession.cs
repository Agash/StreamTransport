using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace Agash.StreamTransport.WebRtc.Srtp;

/// <summary>
/// An SRTP session derived from the DTLS-SRTP keying material (RFC 5764). It derives the per-direction
/// session keys (RFC 3711 KDF), tracks the rollover counter per SSRC, and protects/unprotects RTP packets
/// with the negotiated AES-GCM suite (RFC 7714). One instance per peer connection; the DTLS role decides
/// which master key encrypts the outbound direction.
/// </summary>
public sealed class SrtpSession
{
    private readonly byte[] _sendKey;
    private readonly byte[] _sendSalt;
    private readonly byte[] _recvKey;
    private readonly byte[] _recvSalt;
    private readonly byte[] _sendRtcpKey;
    private readonly byte[] _sendRtcpSalt;
    private readonly byte[] _recvRtcpKey;
    private readonly byte[] _recvRtcpSalt;
    private readonly ConcurrentDictionary<uint, SenderRollover> _sendRoc = new();
    private readonly ConcurrentDictionary<uint, ReceiverRollover> _recvRoc = new();
    private int _srtcpSendIndex;

    /// <summary>
    /// Builds the session from exported keying material. <paramref name="isDtlsClient"/> selects which
    /// master key/salt protects the outbound direction: the DTLS client sends with the client write keys,
    /// the server with the server write keys (RFC 5764 §4.2).
    /// </summary>
    public SrtpSession(SrtpKeyingMaterial keying, bool isDtlsClient)
    {
        int keyLength = keying.Profile switch
        {
            SrtpProtectionProfile.AeadAes128Gcm => 16,
            SrtpProtectionProfile.AeadAes256Gcm => 32,
            _ => throw new NotSupportedException($"Only AES-GCM SRTP profiles are supported, not {keying.Profile}."),
        };

        ReadOnlySpan<byte> sendMasterKey = (isDtlsClient ? keying.ClientMasterKey : keying.ServerMasterKey).Span;
        ReadOnlySpan<byte> sendMasterSalt = (isDtlsClient ? keying.ClientMasterSalt : keying.ServerMasterSalt).Span;
        ReadOnlySpan<byte> recvMasterKey = (isDtlsClient ? keying.ServerMasterKey : keying.ClientMasterKey).Span;
        ReadOnlySpan<byte> recvMasterSalt = (isDtlsClient ? keying.ServerMasterSalt : keying.ClientMasterSalt).Span;

        _sendKey = SrtpKeyDerivation.Derive(sendMasterKey, sendMasterSalt, SrtpKeyDerivation.LabelRtpEncryption, keyLength);
        _sendSalt = SrtpKeyDerivation.Derive(sendMasterKey, sendMasterSalt, SrtpKeyDerivation.LabelRtpSalt, SrtpGcmTransform.SaltLength);
        _recvKey = SrtpKeyDerivation.Derive(recvMasterKey, recvMasterSalt, SrtpKeyDerivation.LabelRtpEncryption, keyLength);
        _recvSalt = SrtpKeyDerivation.Derive(recvMasterKey, recvMasterSalt, SrtpKeyDerivation.LabelRtpSalt, SrtpGcmTransform.SaltLength);
        _sendRtcpKey = SrtpKeyDerivation.Derive(sendMasterKey, sendMasterSalt, SrtpKeyDerivation.LabelRtcpEncryption, keyLength);
        _sendRtcpSalt = SrtpKeyDerivation.Derive(sendMasterKey, sendMasterSalt, SrtpKeyDerivation.LabelRtcpSalt, SrtpGcmTransform.SaltLength);
        _recvRtcpKey = SrtpKeyDerivation.Derive(recvMasterKey, recvMasterSalt, SrtpKeyDerivation.LabelRtcpEncryption, keyLength);
        _recvRtcpSalt = SrtpKeyDerivation.Derive(recvMasterKey, recvMasterSalt, SrtpKeyDerivation.LabelRtcpSalt, SrtpGcmTransform.SaltLength);
    }

    /// <summary>The bytes added to an RTP packet by protection (the GCM tag).</summary>
    public static int ProtectionOverhead => SrtpGcmTransform.TagLength;

    /// <summary>
    /// Encrypts an RTP packet in place (header authenticated, payload encrypted, tag appended), returning
    /// the protected length. <paramref name="packet"/> needs <see cref="ProtectionOverhead"/> spare octets.
    /// </summary>
    public int ProtectRtp(Span<byte> packet, int length)
    {
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(8, 4));
        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        uint roc = _sendRoc.GetOrAdd(ssrc, static _ => new SenderRollover()).Advance(seq);
        return SrtpGcmTransform.ProtectRtp(_sendKey, _sendSalt, roc, packet, length);
    }

    /// <summary>
    /// Authenticates and decrypts a protected RTP packet in place, writing the recovered plaintext length
    /// to <paramref name="plaintextLength"/>. Returns <see langword="false"/> on authentication failure.
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
        uint roc = _recvRoc.GetOrAdd(ssrc, static _ => new ReceiverRollover()).Estimate(seq);
        return SrtpGcmTransform.UnprotectRtp(_recvKey, _recvSalt, roc, packet, length, out plaintextLength);
    }

    /// <summary>The bytes added to an RTCP packet by protection (the SRTCP index trailer + GCM tag).</summary>
    public static int RtcpProtectionOverhead => SrtpGcmTransform.RtcpOverhead;

    /// <summary>Encrypts an RTCP packet in place (SRTCP), assigning the next outbound SRTCP index.</summary>
    public int ProtectRtcp(Span<byte> packet, int length)
    {
        uint index = (uint)(Interlocked.Increment(ref _srtcpSendIndex) & 0x7FFF_FFFF);
        return SrtpGcmTransform.ProtectRtcp(_sendRtcpKey, _sendRtcpSalt, index, packet, length);
    }

    /// <summary>Authenticates and decrypts an SRTCP packet in place, writing the recovered RTCP length.</summary>
    public bool UnprotectRtcp(Span<byte> packet, int length, out int plaintextLength) =>
        SrtpGcmTransform.UnprotectRtcp(_recvRtcpKey, _recvRtcpSalt, packet, length, out plaintextLength);

    private sealed class SenderRollover
    {
        private uint _roc;
        private ushort _lastSeq;
        private bool _seen;

        public uint Advance(ushort seq)
        {
            if (_seen && seq < _lastSeq && _lastSeq - seq > 0x8000)
            {
                _roc++;
            }

            _lastSeq = seq;
            _seen = true;
            return _roc;
        }
    }

    private sealed class ReceiverRollover
    {
        private uint _roc;
        private ushort _highestSeq;
        private bool _seen;

        // RFC 3711 §3.3.1 packet-index guessing.
        public uint Estimate(ushort seq)
        {
            if (!_seen)
            {
                _seen = true;
                _highestSeq = seq;
                return _roc;
            }

            uint v;
            if (_highestSeq < 0x8000)
            {
                v = seq - _highestSeq > 0x8000 ? unchecked(_roc - 1) : _roc;
            }
            else
            {
                v = _highestSeq - 0x8000 > seq ? unchecked(_roc + 1) : _roc;
            }

            if (v == _roc + 1 || (v == _roc && seq > _highestSeq))
            {
                _roc = v;
                _highestSeq = seq;
            }

            return v;
        }
    }
}
