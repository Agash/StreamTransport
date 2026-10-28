using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.WebRtc.Ice;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc;

// ECN for RTP (RFC 6679, with RFC 8888 section 7's congestion feedback in place of ECN feedback packets):
// negotiated with a=ecn-capable-rtp using in-band initiation, marked only on media and only once both sides
// agreed and this side can set and the peer can read the field, validated on the path before all media is
// marked and checked for as long as it is, and tested again when ICE moves to another path. The codepoint is
// ECT(1) when the peer asked for it and the controller answers scalably, ECT(0) otherwise (RFC 6679 section
// 7.3.1, RFC 9331 section 4.3).
public sealed partial class PeerConnection
{
    private readonly EcnValidator _ecnValidator = new();
    private EcnCodepoint _ecnAgreed;

    /// <summary>Raised when the path's ECN validation state changes.</summary>
    public event Action<EcnState>? EcnStateChanged;

    /// <summary>Where the path's ECN support stands: disabled until agreed, then tested, then used or failed.</summary>
    public EcnState EcnState
    {
        get
        {
            lock (_feedbackGate)
            {
                return _ecnValidator.State;
            }
        }
    }

    // What this side's sockets can do with the ECN field; nothing without a controller, since a sender that
    // marks must respond to CE.
    private EcnSupport LocalEcnSupport =>
        _controller is null
            ? EcnSupport.None
            : (_options.SocketFactory?.Ecn ?? new UdpIceSocketFactory().Ecn);

    // The attribute this side offers: in-band initiation, its mode, and a preference for ECT(1).
    private SdpEcnCapability? LocalEcnCapability =>
        LocalEcnSupport switch
        {
            EcnSupport.None => null,
            EcnSupport.SetOnly => new SdpEcnCapability(["rtp"], "setonly", "1"),
            EcnSupport.ReadOnly => new SdpEcnCapability(["rtp"], "readonly", "1"),
            _ => new SdpEcnCapability(["rtp"], "setread", "1"),
        };

    // The answer to an offer's attribute: in-band initiation when offered and when the two modes leave a
    // direction ECN can run in (RFC 6679 section 6.1.1).
    private SdpEcnCapability? AnswerEcn(SdpEcnCapability? offered)
    {
        if (
            offered is null
            || !offered.Methods.Contains("rtp")
            || LocalEcnCapability is not { } local
        )
        {
            return null;
        }

        bool anyDirection = (local.CanSet && offered.CanRead) || (offered.CanSet && local.CanRead);
        return anyDirection ? local : null;
    }

    // Once the remote description says what the peer can do: whether this side marks, with which codepoint,
    // and the validator started or stopped.
    private void NoteEcn(SdpDescription remote)
    {
        SdpEcnCapability? theirs = null;
        foreach (SdpMediaDescription media in remote.Media)
        {
            theirs ??= media.Ecn;
        }

        bool mayMark =
            theirs is not null
            && theirs.Methods.Contains("rtp")
            && theirs.CanRead
            && LocalEcnCapability is { CanSet: true }
            && _ccfbNegotiated;
        lock (_feedbackGate)
        {
            _ecnAgreed =
                !mayMark ? EcnCodepoint.NotEct
                : theirs!.Ect == "1" ? EcnCodepoint.Ect1
                : EcnCodepoint.Ect0;
            if (mayMark)
            {
                _ecnValidator.Start();
            }
            else
            {
                _ecnValidator.Disable();
            }

            _controller?.UseEcn(_ecnAgreed);
        }
    }

    // Under _feedbackGate: the codepoint a media packet carries.
    private EcnCodepoint MarkForEcn(long packetId)
    {
        if (_ecnAgreed == EcnCodepoint.NotEct || _controller is not { } controller)
        {
            return EcnCodepoint.NotEct;
        }

        // ECT(1) only when the peer asked for it and the controller answers scalably.
        EcnCodepoint wanted =
            _ecnAgreed == EcnCodepoint.Ect1 && controller.Ecn == EcnCodepoint.Ect1
                ? EcnCodepoint.Ect1
                : EcnCodepoint.Ect0;
        return _ecnValidator.MarkFor(packetId, wanted, Now);
    }

    // Under _feedbackGate: the validator follows the feedback, and the controller the validator.
    private void CheckEcn(ReadOnlySpan<PacketObservation> observations)
    {
        if (_ecnAgreed != EcnCodepoint.NotEct)
        {
            _ecnValidator.OnFeedback(observations, Now);
        }
    }

    private void OnEcnState(EcnState state)
    {
        _controller?.UseEcn(
            state is EcnState.Testing or EcnState.Capable ? _ecnAgreed : EcnCodepoint.NotEct
        );
        EcnStateChanged?.Invoke(state);
    }

    // A new path may not carry what the old one did.
    private void OnPathChanged(IcePath? path)
    {
        if (path is { } next)
        {
            if (_lastPath is { } previous && !SameAddresses(previous, next))
            {
                RestartCongestionControl(next);
            }

            _lastPath = next;
        }

        lock (_feedbackGate)
        {
            if (_ecnAgreed != EcnCodepoint.NotEct && path is not null)
            {
                _ecnValidator.Start();
            }
        }

        if (path is { } selected)
        {
            SelectPathMtu(selected);
        }
    }
}
