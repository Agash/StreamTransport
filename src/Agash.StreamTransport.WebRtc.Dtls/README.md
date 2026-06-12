# Agash.StreamTransport.WebRtc.Dtls

The DTLS 1.2 + `use_srtp` (RFC 5764) handshake for the
[Agash.StreamTransport](https://github.com/Agash/StreamTransport) WebRTC stack. It implements
`IDtlsTransportFactory` from `Agash.StreamTransport.WebRtc.Abstractions`: it generates a self-signed
ECDSA certificate, runs the handshake over a caller-supplied datagram path (the ICE transport),
authenticates the peer by certificate fingerprint, and exports the SRTP keying material.

This is the **only** package in the stack that depends on BouncyCastle. The WebRTC core uses BCL crypto
only; DTLS is quarantined here behind the factory interface, so a first-party BCL DTLS engine can later
replace it as a one-package swap. See ADR-0003 in the repository.
