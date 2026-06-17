# Agash.StreamTransport

Local-first, peer-to-peer real-time media transport for .NET. It moves GPU video frames and synchronised
audio between machines over WebRTC (hardware H.265 video, Opus audio, one `RTCPeerConnection`) on a
first-party, dependency-light WebRTC stack (ICE / STUN / DTLS-SRTP / RTP / RTCP / SDP) built on the BCL, with
no SIP stack and NativeAOT support. The capture layer and the signaling channel are both abstracted, so the
host application owns where frames come from and how peers find each other.

```
capture (Spout / Syphon / PipeWire / camera) -> HW H.265 encode -> WebRTC P2P -> HW H.265 decode -> publish
```

Targets `net11.0`, with per-OS agent heads — Windows (`net11.0-windows`, D3D11/Spout), macOS (`net11.0-macos`,
Metal/IOSurface/Syphon + VideoToolbox + CoreAudio), and Linux (`net11.0`, VAAPI/PipeWire/Vulkan). All three
publish as **self-contained, trimmed CoreCLR NativeAOT** binaries. The transport itself never touches a disk, a
tunnel, or a capture API; those live behind two seams the host fills in.

> ### Status: v0.1.0-alpha1
>
> The core stack (signaling, ICE, DTLS-SRTP, RTP/RTCP, SDP, congestion control, loss recovery, mobility) is
> implemented and covered by unit, in-process E2E, and loopback tests on Windows, macOS, and Linux CI, plus a
> 3-machine cross-platform verify matrix (`eng/verify-matrix.ps1`) that drives the NativeAOT binaries.
> Hardware encode/decode is verified on **NVENC (Windows/Linux)**, **AMF (Windows)**, **VideoToolbox (macOS,
> via a raw `VTDecompressionSession` decode path)**, and **VAAPI + PipeWire DMA-BUF (Linux)** — the Linux GPU
> path is now hardware-verified on a real handheld, not just CI. Cross-machine 1080p60 sustains full frame rate
> over a lossy Wi-Fi link (sequence-aware H.265 reassembly + NACK/RTX recovery). AV1 is registered as a codec
> but has no real-time hardware encoder on current GPUs, so it is decode/software only for now.

## The two seams

1. **Capture** - `IVideoFrameSource` / `IVideoFrameSink` / `IAudioFrameSource`. The transport never knows where
   frames originate or go. The GPU interop libraries live in the consumer, so the same transport serves a full
   desktop app and a headless single-board-computer field agent.
2. **Signaling** - `ISignalingChannel`. The transport never knows how SDP/ICE are delivered; the host owns
   reachability (a WebSocket, a SignalR hub, a tunnel). Tunnel and host concerns never enter the library.

## Resilience

Built for lossy, mobile uplinks as well as clean LANs, selected by a single media profile (`InteractiveP2P`,
`ScreenShare`, `IrlContribution`):

- **Congestion control** - a SCReAM controller (RFC 8298) driven by RTCP Congestion Control Feedback
  (RFC 8888); the encoder is retuned and the sender paced to the estimate.
- **Loss recovery** - a sequence-aware H.265 packet buffer reorders RTP into complete frames (so a lost packet
  never feeds the decoder a corrupt/merged access unit), NACK + RTX (RFC 4588) retransmit holes in order on
  low-RTT links, with a throttled keyframe request on unrecoverable gaps; FlexFEC (RFC 8627) on the high-RTT
  IRL profile, where a retransmit round trip is too slow.
- **Mobility** - full credential-rollover ICE restart and pre-warmed hot-standby candidate switching, with the
  DTLS-SRTP session (keys + rollover counter) preserved across a path change.

## Packages

| Package | What it is |
|---|---|
| `Agash.StreamTransport.Abstractions` | Capture + signaling contracts, codec/interop types. Zero deps beyond the BCL. |
| `Agash.StreamTransport` | The transport: first-party WebRTC + FFmpeg hardware codecs + room client. Ships the per-RID FFmpeg natives. |
| `Agash.StreamTransport.Signaling` | Transport-agnostic room router + a WebSocket signaling transport. |
| `Agash.StreamTransport.Stun` | Single-port STUN binding server + ICE server providers (static + coturn ephemeral creds). |
| `Agash.StreamTransport.WebRtc.Abstractions` | Transport seams for the WebRTC stack (ICE/DTLS/SRTP roles, options, diagnostics). |
| `Agash.StreamTransport.WebRtc` | The WebRTC core: ICE, STUN, SRTP-GCM, RTP/RTCP, SDP, `PeerConnection`. BCL crypto only. |
| `Agash.StreamTransport.WebRtc.Dtls` | DTLS-SRTP handshake behind `IDtlsTransportFactory` (the one BouncyCastle dependency). |
| `Agash.StreamTransport.WebRtc.CongestionControl` | The SCReAM `INetworkController` and pacing. |
| `Agash.StreamTransport.WebRtc.DependencyInjection` | `AddStreamTransportWebRtc()` wiring. |

## Capture companions

The GPU interop libraries that feed the capture seam are published separately and pair with
`IVideoFrameSource` / `IVideoFrameSink`:

| Library | Platform | Mechanism |
|---|---|---|
| [Spout2.NET](https://github.com/Agash/Spout2.NET) | Windows | Spout (DirectX 11 shared texture) |
| [Syphon.NET](https://github.com/Agash/Syphon.NET) | macOS | Syphon (IOSurface / Metal) |
| [PipeWire.NET](https://github.com/Agash/PipeWire.NET) | Linux | PipeWire (DMA-BUF) |

## Audio and sync

Audio is a first-class track: the sender encodes Opus (pure-managed, no native dependency) and carries it
alongside video on the one peer connection. Both tracks are stamped from a common monotonic capture clock, and
the receiver schedules playout against abs-capture-time plus RTCP sender reports, so the two stay in lip-sync.

## Build

Needs the .NET 11 preview SDK. FFmpeg 8.1 natives are fetched per RID before building anything that touches
codecs:

```bash
./eng/fetch-ffmpeg.ps1 -Rids win-x64        # or linux-x64 / osx-arm64 / linux-arm64
dotnet build StreamTransport.slnx -c Release
dotnet test  StreamTransport.slnx -c Release --filter "TestCategory!=Integration"
```

## Try it: relay + two agents

A self-hostable signaling relay and a sender/receiver agent live in `samples/`. The fastest local test:

```bash
dotnet run --project samples/StreamTransport.Relay                                   # WS signaling :8080/ws + STUN :3478
dotnet run --project samples/StreamTransport.Agent -- send    --relay ws://localhost:8080/ws --room demo --profile irl
dotnet run --project samples/StreamTransport.Agent -- receive --relay ws://localhost:8080/ws --room demo --profile irl
```

The agent is the reference capture wiring and the cross-platform end-to-end test. See
[`samples/StreamTransport.Agent/README.md`](samples/StreamTransport.Agent/README.md) for full install and
configuration: providing the correct FFmpeg build (even when a different one is already on the machine),
setting up Spout / Syphon / PipeWire capture, selecting cameras and microphones, and publishing into OBS.

## License

MIT. See [LICENSE](LICENSE).
