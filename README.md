# StreamTransport

[![NuGet](https://img.shields.io/nuget/v/Agash.StreamTransport.svg)](https://www.nuget.org/packages/Agash.StreamTransport)
[![CI](https://github.com/Agash/StreamTransport/actions/workflows/ci.yml/badge.svg)](https://github.com/Agash/StreamTransport/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Local-first, peer-to-peer real-time media for .NET. Move hardware-encoded H.265 video and Opus audio between
machines over WebRTC, on a first-party WebRTC stack (ICE / STUN / DTLS-SRTP / RTP / RTCP / SDP) built on the
BCL, no SIP stack, NativeAOT-friendly.

```
capture (camera / Spout / Syphon / PipeWire) -> HW H.265 -> WebRTC P2P -> HW H.265 -> publish
```

> ### ⚠️ Alpha, early and rough
> This is an early `0.1.0-alpha`. The core works and is exercised by tests, but it is **largely untested in
> the real world**, unpolished in places, and has lots of room to improve. Expect breaking changes and sharp
> edges. Please try it and file issues, just don't ship it to production yet.

## What you get

- A complete, dependency-light **WebRTC** stack: ICE, STUN, DTLS-SRTP (GCM), RTP/RTCP, SDP/JSEP, and a
  `PeerConnection`, BCL crypto, with one BouncyCastle dependency for the DTLS handshake.
- **Hardware H.265** encode/decode through FFmpeg (NVENC, AMF, QSV, VAAPI, VideoToolbox) with a software
  fallback, plus pure-managed **Opus** audio on the same connection, kept in lip-sync.
- **Resilience for real links**: SCReAM congestion control (RFC 8298 / 8888), sequence-aware H.265 reassembly,
  NACK/RTX and FlexFEC loss recovery, and ICE-restart / hot-standby mobility, selected by one media profile
  (`InteractiveP2P`, `ScreenShare`, `IrlContribution`).
- **NativeAOT-friendly**: self-contained, trimmed binaries on Windows, Linux, and macOS.

## Two seams the host fills in

- **Capture**, `IVideoFrameSource` / `IVideoFrameSink` / `IAudioFrameSource`. The transport never knows where
  frames come from, so the GPU-interop libraries live in the consumer.
- **Signaling**, `ISignalingChannel`. The transport never knows how SDP/ICE are delivered (a WebSocket, a
  SignalR hub, a tunnel); the host owns reachability.

## Capture companions

Zero-copy GPU sharing libraries that feed the capture seam, published separately:

| Library | Platform | Mechanism |
|---|---|---|
| [Spout2.NET](https://github.com/Agash/Spout2.NET) | Windows | Spout (DirectX 11 shared texture) |
| [Syphon.NET](https://github.com/Agash/Syphon.NET) | macOS | Syphon (IOSurface / Metal) |
| [PipeWire.NET](https://github.com/Agash/PipeWire.NET) | Linux | PipeWire (DMA-BUF) |

## Packages

| Package | What it is |
|---|---|
| `Agash.StreamTransport` | The transport: WebRTC + FFmpeg hardware codecs + room client. |
| `Agash.StreamTransport.Abstractions` | Capture + signaling contracts. BCL only. |
| `Agash.StreamTransport.WebRtc` | The WebRTC core: ICE, STUN, SRTP, RTP/RTCP, SDP, `PeerConnection`. |
| `Agash.StreamTransport.WebRtc.Abstractions` | Seams for the WebRTC stack. |
| `Agash.StreamTransport.WebRtc.Dtls` | DTLS-SRTP handshake (the one BouncyCastle dependency). |
| `Agash.StreamTransport.WebRtc.CongestionControl` | SCReAM controller + pacing. |
| `Agash.StreamTransport.WebRtc.DependencyInjection` | `AddStreamTransportWebRtc()` wiring. |
| `Agash.StreamTransport.Signaling` | Room router + a WebSocket signaling transport. |
| `Agash.StreamTransport.Stun` | STUN binding server + ICE server providers. |

## Build

Needs the .NET 11 preview SDK. FFmpeg 8.1 natives are fetched per platform first:

```bash
./eng/fetch-ffmpeg.ps1 -Rids win-x64        # or linux-x64 / linux-arm64 / osx-arm64
dotnet build StreamTransport.slnx -c Release
dotnet test  StreamTransport.slnx -c Release --filter "TestCategory!=Integration"
```

## Try it

A self-hostable signaling relay and a sender/receiver agent live in `samples/`:

```bash
dotnet run --project samples/StreamTransport.Relay                                       # WS :8080/ws + STUN :3478
dotnet run --project samples/StreamTransport.Agent -- send    --relay ws://localhost:8080/ws --room demo
dotnet run --project samples/StreamTransport.Agent -- receive --relay ws://localhost:8080/ws --room demo
```

See [`samples/StreamTransport.Agent`](samples/StreamTransport.Agent) for capture setup (Spout / Syphon /
PipeWire), device selection, and publishing into OBS.

## License

MIT. See [LICENSE](LICENSE).
