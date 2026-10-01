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

## Diagnostics

The meter and activity source `Agash.StreamTransport.WebRtc` (`WebRtcDiagnostics`). With DI the meter
comes from the host's `IMeterFactory`; `PeerConnection` and `IceAgent` take one too. Nothing is
recorded until something listens.

| Instrument | Unit | What |
|---|---|---|
| `streamtransport.webrtc.connections.active` | `{connection}` | peer connections not yet disposed |
| `streamtransport.webrtc.connect.duration` | `s` | ICE start to DTLS-SRTP up, by `streamtransport.outcome`: `connected`, `ice_failed`, `dtls_failed`, `closed` |
| `streamtransport.webrtc.ice.selected_paths` | `{path}` | selected pairs by local and remote candidate kind (`host`, `srflx`, `prflx`, `relay`) and `network.type` (`ipv6`, `ipv4`) |
| `streamtransport.webrtc.ice.consent_lost` | `{event}` | selected paths that stopped answering consent checks |
| `streamtransport.webrtc.ice.restarts` | `{restart}` | ICE restarts |
| `streamtransport.webrtc.turn.allocations` | `{allocation}` | TURN allocations by `network.transport` and outcome |
| `streamtransport.webrtc.rtp.packets.sent` | `{packet}` | media packets sent |
| `streamtransport.webrtc.nack.sequences` | `{packet}` | lost packets asked for again |
| `streamtransport.webrtc.rtx.packets` | `{packet}` | retransmissions `sent` and `recovered` |

The activity `streamtransport.webrtc.connect` spans ICE and the DTLS handshake, with the selected
path's candidate kinds and address family. DTLS itself reports on the `Dtls.Core` meter.
