using System.Buffers;
using System.Buffers.Text;
using System.Net.Security;
using System.Security.Cryptography;
using Agash.StreamTransport.WebRtc.Ice;
using Agash.StreamTransport.WebRtc.Sdp;
using Agash.StreamTransport.WebRtc.Srtp;
using Dtls.Core;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.WebRtc;

// RFC 8842: a description can ask for a new DTLS association (another tls-id, fingerprint set or role).
// One comes with an ICE restart, which gives it its own 5-tuple (sections 5.1 and 6): its handshake runs
// on the restarted session's pair while the old association keeps carrying media, records of the previous
// session's sockets still go to the old one, and once the new handshake is done media moves to its keys.
// For a while after the switch the old keys still open what was sent under them.
public sealed partial class PeerConnection
{
    // The association a description asked for, whose handshake runs once the restarted ICE session has a
    // pair; null when none is pending.
    private PendingAssociation? _pendingAssociation;

    // The transport of the association replaced last: records of the previous session go here and are
    // dropped.
    private IceDatagramTransport? _replacedTransport;

    // The SRTP keys of the replaced association, and until when they still open what arrives.
    private SrtpSession? _previousSrtp;
    private long _previousSrtpUntil;
    private ITimer? _previousSrtpTimer;

    // This side offered a new association (section 5.5), and the certificate it had before, which stays
    // if the peer keeps the association it has.
    private bool _renewing;
    private RtcCertificate? _renewedFrom;

    /// <summary>
    /// Raised when a new DTLS association has replaced the old one and media runs on its keys
    /// (RFC 8842 section 5).
    /// </summary>
    public event Action<SrtpProtectionProfile>? DtlsAssociationRenewed;

    /// <summary>
    /// Asks for a new DTLS association (RFC 8842 section 5.5): a re-offer with a new tls-id, setup actpass
    /// and an ICE restart, which gives the association its own 5-tuple (section 6). With a certificate, the
    /// new association authenticates with it, as after a certificate rotation. A peer that keeps the
    /// association it has answers with its tls-id unchanged, and nothing changes.
    /// </summary>
    /// <param name="certificate">The certificate for the new association; the current one when null.</param>
    /// <returns>The offer to send.</returns>
    public SdpDescription RenewDtlsAssociation(RtcCertificate? certificate = null)
    {
        if (_association is null)
        {
            throw new InvalidOperationException(
                "There is no DTLS association to renew until the first exchange completes."
            );
        }

        _renewing = true;
        _localTlsId = NewTlsId();
        if (certificate is not null)
        {
            _renewedFrom = _certificate;
            _certificate = certificate;
        }

        LogRenewalOffered(_logger);
        return RestartIce();
    }

    private static string NewTlsId() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(18));

    // A description asked for a new association. Returns whether it is taken: with an ICE restart
    // (an offer with new credentials, or the answer to this side's renewal), it is; without one it is
    // refused, as section 5.3 allows.
    private bool AcceptNewAssociation(
        SdpMediaDescription first,
        DtlsRole role,
        SdpType type,
        bool restart
    )
    {
        if (!(type == SdpType.Offer ? restart : _renewing))
        {
            return false;
        }

        _association = new DtlsAssociation(first.TlsId, [.. first.Fingerprints], role);
        _dtlsRole = role;
        if (type == SdpType.Offer)
        {
            // Section 5.2: an answer that agrees to a new association carries a new tls-id.
            _localTlsId = NewTlsId();
        }

        // Records of the new association arrive on the restarted session's sockets from now; they wait
        // here for its handshake.
        Volatile.Write(
            ref _pendingAssociation,
            new PendingAssociation(new IceDatagramTransport(_iceAgent!), role, _certificate)
        );
        _renewing = false;
        _renewedFrom = null;
        LogNewAssociationTaken(_logger, type, role);
        return true;
    }

    // Section 5.5: the peer kept the association this side offered to replace.
    private void KeepAssociation()
    {
        if (!_renewing)
        {
            return;
        }

        _renewing = false;
        if (_renewedFrom is { } previous)
        {
            _certificate = previous;
            _renewedFrom = null;
        }

        LogRenewalDeclined(_logger);
    }

    // The restarted ICE session has its pair: the new association's handshake starts on it.
    private void OnRestartCompleted()
    {
        if (Volatile.Read(ref _pendingAssociation) is { } pending && pending.TryStart())
        {
            _ = RunReassociationAsync(pending);
        }
    }

    private async Task RunReassociationAsync(PendingAssociation pending)
    {
        RemoteCertificateValidationCallback validate = DtlsFingerprint.CreateValidationCallback(
            _expectedRemoteFingerprints
        );
        DtlsConnection? dtls = null;
        try
        {
            dtls =
                pending.Role == DtlsRole.Client
                    ? await DtlsConnection
                        .ConnectAsync(
                            pending.Transport,
                            ClientOptions(validate, pending.Certificate)
                        )
                        .ConfigureAwait(false)
                    : await DtlsConnection
                        .AcceptAsync(
                            pending.Transport,
                            ServerOptions(validate, pending.Certificate)
                        )
                        .ConfigureAwait(false);
            SrtpKeyingMaterial keying =
                dtls.SrtpKeyingMaterial
                ?? throw new DtlsException("The peer negotiated no SRTP protection profile.");
            SwitchTo(pending, dtls, keying);
            LogAssociationRenewed(_logger, dtls.NegotiatedProtocol, keying.Profile);
            DtlsAssociationRenewed?.Invoke(keying.Profile);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The old association still carries media; the failed one is dropped and the peer can ask again.
            LogReassociationFailed(_logger, exception);
            _ = Interlocked.CompareExchange(ref _pendingAssociation, null, pending);
            pending.Transport.Complete();
            if (dtls is not null)
            {
                await dtls.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // Media moves to the new keys. The old association's transport goes quiet, so nothing more of it
    // reaches the network, and its keys stay to open what was sent under them for the restart's overlap.
    private void SwitchTo(
        PendingAssociation pending,
        DtlsConnection dtls,
        SrtpKeyingMaterial keying
    )
    {
        SrtpSession next = new(keying, pending.Role == DtlsRole.Client)
        {
            SendCryptex = _sendCryptex,
            EncryptedExtensions = Volatile.Read(ref _extensions).Encrypted,
        };
        DtlsConnection? oldDtls;
        lock (_gate)
        {
            oldDtls = _dtls;
            _replacedTransport?.Complete();
            _replacedTransport = _dtlsTransport;
            _replacedTransport?.Complete();
            _dtlsTransport = pending.Transport;
            _dtls = dtls;
            _previousSrtp?.Dispose();
            _previousSrtp = Interlocked.Exchange(ref _srtp, next);
            _previousSrtpUntil = _time.GetTimestamp() + Ticks(IceAgent.RestartOverlap);
            _previousSrtpTimer?.Dispose();
            _previousSrtpTimer = _time.CreateTimer(
                static state => ((PeerConnection)state!).ForgetPreviousKeys(),
                this,
                IceAgent.RestartOverlap,
                Timeout.InfiniteTimeSpan
            );
            _pendingAssociation = null;
        }

        if (oldDtls is not null)
        {
            _ = oldDtls.DisposeAsync().AsTask();
        }
    }

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);

    private void ForgetPreviousKeys()
    {
        lock (_gate)
        {
            _previousSrtp?.Dispose();
            _previousSrtp = null;
            _previousSrtpTimer?.Dispose();
            _previousSrtpTimer = null;
        }
    }

    // Where a DTLS datagram goes: the previous ICE session's sockets carry the association made before the
    // restart, the restarted session's the one a pending renewal makes (section 6).
    private IceDatagramTransport? DtlsTransportFor(bool previousSession) =>
        previousSession
            ? Volatile.Read(ref _replacedTransport) ?? _dtlsTransport
            : Volatile.Read(ref _pendingAssociation)?.Transport ?? _dtlsTransport;

    // SRTP opened in place with the current keys, and in the overlap after a switch with the replaced
    // ones too. A failed attempt leaves the packet changed, so in the overlap the first goes on a copy.
    private bool Unprotect(SrtpSession current, Span<byte> packet, bool rtcp, out int length)
    {
        SrtpSession? previous = Volatile.Read(ref _previousSrtp);
        if (previous is null || _time.GetTimestamp() >= Volatile.Read(ref _previousSrtpUntil))
        {
            return rtcp
                ? current.UnprotectRtcp(packet, packet.Length, out length)
                : current.UnprotectRtp(packet, packet.Length, out length);
        }

        byte[] original = ArrayPool<byte>.Shared.Rent(packet.Length);
        try
        {
            packet.CopyTo(original);
            if (
                rtcp
                    ? current.UnprotectRtcp(packet, packet.Length, out length)
                    : current.UnprotectRtp(packet, packet.Length, out length)
            )
            {
                return true;
            }

            original.AsSpan(0, packet.Length).CopyTo(packet);
            return rtcp
                ? previous.UnprotectRtcp(packet, packet.Length, out length)
                : previous.UnprotectRtp(packet, packet.Length, out length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(original);
        }
    }

    // The new association before its handshake: the transport its records queue on, the role and the
    // certificate it was agreed with, and whether its handshake has started.
    private sealed class PendingAssociation(
        IceDatagramTransport transport,
        DtlsRole role,
        RtcCertificate certificate
    )
    {
        private int _started;

        public IceDatagramTransport Transport { get; } = transport;

        public DtlsRole Role { get; } = role;

        public RtcCertificate Certificate { get; } = certificate;

        public bool TryStart() => Interlocked.Exchange(ref _started, 1) == 0;
    }

    [LoggerMessage(
        EventId = 1106,
        Level = LogLevel.Information,
        Message = "Offering a new DTLS association with an ICE restart (RFC 8842 section 5.5)"
    )]
    private static partial void LogRenewalOffered(ILogger logger);

    [LoggerMessage(
        EventId = 1107,
        Level = LogLevel.Information,
        Message = "The {Type} asks for a new DTLS association as {Role}; it is made on the restarted ICE session"
    )]
    private static partial void LogNewAssociationTaken(ILogger logger, SdpType type, DtlsRole role);

    [LoggerMessage(
        EventId = 1108,
        Level = LogLevel.Information,
        Message = "The peer kept the DTLS association this side offered to replace (RFC 8842 section 5.5)"
    )]
    private static partial void LogRenewalDeclined(ILogger logger);

    [LoggerMessage(
        EventId = 1109,
        Level = LogLevel.Information,
        Message = "New DTLS association ({Protocol}, {Profile}); media moved to its keys"
    )]
    private static partial void LogAssociationRenewed(
        ILogger logger,
        DtlsProtocols protocol,
        SrtpProtectionProfile profile
    );

    [LoggerMessage(
        EventId = 1143,
        Level = LogLevel.Warning,
        Message = "The new DTLS association failed; media stays on the old one"
    )]
    private static partial void LogReassociationFailed(ILogger logger, Exception exception);
}
