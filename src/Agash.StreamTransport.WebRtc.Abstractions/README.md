# Agash.StreamTransport.WebRtc.Abstractions

Leaf contracts for the first-party WebRTC stack used by
[`Agash.StreamTransport`](https://github.com/Agash/StreamTransport): ICE/DTLS roles, SRTP protection
profiles, DTLS fingerprints, and the SRTP keying material a DTLS-SRTP handshake exports.

Its only dependency beyond the BCL is `Microsoft.Extensions.Logging.Abstractions`. Reference this package
to consume the transport's seams (for example to provide an alternative DTLS implementation) without
pulling in crypto or `Microsoft.Extensions.*` hosting.


