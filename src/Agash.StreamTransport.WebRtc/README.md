# Agash.StreamTransport.WebRtc

A small, modern WebRTC stack for .NET 11 with the parts a point-to-point media transport uses: ICE/STUN,
SRTP, RTP/RTCP, SDP/JSEP, a peer connection, and loss recovery (NACK with RTX retransmission, PLI keyframe
requests, FlexFEC, and a sequence-aware H.265 packet buffer that reassembles complete frames before decode).
Behaviour follows libwebrtc where interop and correctness depend on it: the NACK requester, RTX unwrap and
`h26x_packet_buffer` are ports of their libwebrtc counterparts. The code is idiomatic modern .NET (spans,
`ArrayPool`, `readonly record struct`, source-generated logging).

SRTP offers `SRTP_AEAD_AES_128_GCM`, `SRTP_AEAD_AES_256_GCM` and, for legacy peers,
`SRTP_AES128_CM_HMAC_SHA1_80`, with a 1024-packet replay window per SSRC.

Crypto is the platform's own (`AesGcm`, `Aes`, `HMACSHA1`, `CertificateRequest`). DTLS 1.2 and 1.3 come from
[Dtls.Core](https://github.com/Agash/Dtls.Core), which is built on the same primitives, so there is no native
or third-party crypto dependency.

The package is the transport under [`Agash.StreamTransport`](https://github.com/Agash/StreamTransport).
