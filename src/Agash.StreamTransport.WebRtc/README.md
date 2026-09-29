# Agash.StreamTransport.WebRtc

A small, modern WebRTC stack for .NET 11 - the parts a point-to-point media transport actually uses:
ICE/STUN, SRTP, RTP/RTCP, SDP/JSEP, a peer connection, and loss recovery (NACK + RTX retransmission, PLI
keyframe requests, FlexFEC, and a sequence-aware H.265 packet buffer that reassembles complete frames before
decode). Behaviour is aligned with libwebrtc where interop and correctness require it - the NACK requester, RTX
unwrap, and `h26x_packet_buffer` are ports of their libwebrtc counterparts; the shape is idiomatic modern .NET
(spans, `ArrayPool`, `readonly record struct`, source-generated logging).

Crypto is the platform's own (`AesGcm`, `Aes`/`HMACSHA1`, `CertificateRequest`). DTLS 1.2 and 1.3 come
from [Dtls.NET](https://github.com/Agash/Dtls.NET), which is built on the same primitives, so there is no
native or third-party crypto dependency.

This is the first-party transport that replaces SIPSorcery in
[`Agash.StreamTransport`](https://github.com/Agash/StreamTransport).
