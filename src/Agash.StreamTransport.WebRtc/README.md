# Agash.StreamTransport.WebRtc

A small, modern WebRTC stack for .NET 11 - the parts a point-to-point media transport actually uses:
ICE/STUN, SRTP, RTP/RTCP, SDP/JSEP, and a peer connection. Behaviour is aligned with libwebrtc where
interop and correctness require it; the shape is idiomatic modern .NET (spans, `ArrayPool`,
`readonly record struct`, source-generated logging).

Crypto uses the BCL only (`AesGcm`, `Aes`/`HMACSHA1`, `CertificateRequest`). DTLS - the one piece the BCL
does not provide - is supplied by `Agash.StreamTransport.WebRtc.Dtls` (BouncyCastle, isolated there) via a
pluggable factory, so this package itself has no third-party runtime dependency.

This is the first-party transport that replaces SIPSorcery in
[`Agash.StreamTransport`](https://github.com/Agash/StreamTransport). See ADR-0003 in the repository.
