# StreamTransport

[![NuGet](https://img.shields.io/nuget/v/Agash.StreamTransport.svg)](https://www.nuget.org/packages/Agash.StreamTransport)
[![CI](https://github.com/Agash/StreamTransport/actions/workflows/ci.yml/badge.svg)](https://github.com/Agash/StreamTransport/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Peer-to-peer real-time media for .NET over WebRTC: video in H.264, H.265 or AV1 and Opus audio between
machines, on a first-party WebRTC stack (ICE, STUN, TURN, DTLS-SRTP, RTP/RTCP, SDP) built on the BCL and
NativeAOT-friendly.

```
source (camera, screen, Spout, Syphon, PipeWire) -> encoder -> WebRTC -> decoder -> sink
```

> ### Alpha
> The APIs are still changing. The core is exercised by tests on Windows, Linux and macOS; try it and file
> issues, and keep it out of production for now.

## What you get

- **Inputs and outputs** through one registry, `MediaDevices`: cameras (V4L2, Media Foundation,
  AVFoundation, and FFmpeg's devices as the fallback), shared application outputs (Spout, Syphon,
  PipeWire), audio devices (WASAPI, Core Audio, PipeWire), webcams out (v4l2loopback) and a test signal.
  Providers are registered in DI; a host adds its own (NDI, a game capture) the same way, and a device two
  providers reach is listed once and falls back to the next provider when the first cannot open it.

- **Sessions** with a peer over any signaling channel: `IMediaSessionFactory` makes an `IMediaSession` from
  sources to send and sinks to receive into. Rooms (`MediaPublisher`, `MediaSubscriber`) run sessions per
  peer over a relay.
- **Codecs as plug-ins**: encoders, decoders and video processors come from factories registered in DI,
  chosen by what they can do and by rank, with runtime fallback when a driver refuses. `Codecs.FFmpeg`
  brings H.264, H.265 and AV1 on every hardware API FFmpeg 9 has (Direct3D 12, NVENC, AMF, QSV, Vulkan
  Video, VA-API, VideoToolbox) and in software; `Codecs.Opus` brings Opus. A host adds its own codec by
  registering its factories and an RTP payload format.
- **Zero-copy frames**: sources push frames in GPU memory (Direct3D 12 and 11 textures, DMA-BUFs,
  IOSurfaces) or system memory, and the pipeline converts only where an encoder or sink cannot take them.
- **Real links**: SCReAM congestion control (RFC 8298, RFC 8888), a pacer, sequence-aware frame assembly,
  NACK/RTX and FlexFEC, Opus in-band FEC and concealment, ICE restart and hot-standby mobility, IPv6-first
  candidates, TURN over UDP, TCP and TLS with a relay-only policy, and SRTP with AES-GCM (AES-CM for
  legacy peers) and replay protection. Rate control plans for the source's measured frame rate, and a
  receiver that falls behind resyncs at a keyframe instead of building latency.
- **Lip sync**: capture times come from the producer where it reports them (V4L2, Media Foundation,
  AVFoundation, PipeWire) and travel as abs-capture-time; synced playout holds audio and video in one
  adaptive buffer and releases them by capture time. `TestSignalAnalyzer` measures the A/V offset and
  end-to-end latency of a received test signal.
- **Profiles**: `InteractiveP2P`, `ScreenShare`, `IrlContribution` and `AvatarTransparent` set codec
  preference, rate control, repair and playout in one choice. `AvatarTransparent` carries alpha as the
  H.265 alpha layer where both peers code it and side by side through any codec otherwise, packed and
  unpacked by compute shaders.

## Use it

```csharp
services.AddStreamTransport().AddFFmpegCodecs().AddOpusCodecs();

IMediaSessionFactory sessions = provider.GetRequiredService<IMediaSessionFactory>();
await using IMediaSession session = sessions.Create(
    signaling,                         // any ISignalingChannel
    MediaSessionRole.Offerer,
    new MediaEndpoints { VideoSource = camera, AudioSource = microphone },
    MediaSessionOptions.For(MediaProfile.InteractiveP2P)
);
await session.StartAsync();
await session.Connected;
```

Inputs and outputs come from `MediaDevices`, by spec (`provider:name`, a name, or a kind such as
`camera`):

```csharp
MediaDevices devices = provider.GetRequiredService<MediaDevices>();
using IVideoInput camera = await devices.OpenVideoInputAsync("camera", new VideoInputRequest { Size = new(1920, 1080) });
using IAudioInput microphone = await devices.OpenAudioInputAsync("default");
using IVideoOutput spout = await devices.CreateVideoOutputAsync("spout", "Guest");
```

Sources implement `IVideoSource`/`IAudioSource` and push frames; sinks implement `IVideoSink`/`IAudioSink`
and say what they accept. A provider (`IVideoInputProvider`, `IVideoOutputProvider` and the audio pair)
makes them reachable through `MediaDevices`; `VideoInput`, `VideoOutput`, `AudioInput` and `AudioOutput`
wrap any source or sink for one.

## Diagnostics

Logging goes through `ILogger` with source-generated messages and an event id per message; ids are
allocated per package (see `CONTRIBUTING.md`). Metrics and traces cost nothing until something listens.
With DI the meters come from the host's `IMeterFactory`.

| Meter and activity source | Package |
|---|---|
| `Agash.StreamTransport` | sessions, streams, playout |
| `Agash.StreamTransport.Codecs.FFmpeg` | encoder opens and their reasons, codec probes |

Instruments on `Agash.StreamTransport`, tagged with `streamtransport.codec` and, where it applies,
`streamtransport.implementation`, `streamtransport.session.role`, `streamtransport.outcome`,
`streamtransport.reason` or `streamtransport.direction`:

| Instrument | Unit | What |
|---|---|---|
| `streamtransport.sessions.active` | `{session}` | sessions created and not yet disposed |
| `streamtransport.session.connect.duration` | `s` | start to media flowing, or to failure |
| `streamtransport.video.frames.sent` | `{frame}` | encoded frames handed to the link |
| `streamtransport.video.frames.dropped` | `{frame}` | source frames not encoded (`encoder_busy`) |
| `streamtransport.video.encode.duration` | `s` | one frame's encode, by encoder |
| `streamtransport.video.encoder.failures` | `{failure}` | encoders passed over before their first frame |
| `streamtransport.video.target_bitrate` | `bit/s` | the latest rate congestion control asked for |
| `streamtransport.video.frames.decoded` / `.failed` / `.skipped` | `{frame}` | received frames by fate |
| `streamtransport.video.decode.duration` | `s` | one frame's decode |
| `streamtransport.video.keyframe_requests` | `{request}` | sent to or received from the peer |
| `streamtransport.audio.frames.sent` / `.decoded` | `{frame}` | audio frames |
| `streamtransport.audio.frames.repaired` | `{frame}` | lost audio, `concealed` or `recovered` |
| `streamtransport.playout.delay` | `s` | synced playout depth per video frame |

The activity `streamtransport.session.connect` spans a session from `StartAsync` to media flowing,
with the role and the outcome.

## Packages

| Package | What it is |
|---|---|
| `Agash.StreamTransport` | Sessions, streams, pacing, sync, rooms, codec registry, `MediaDevices`, the test signal, DI. |
| `Agash.StreamTransport.Media` | Frames, storages, time, and the codec, processor, input, output, source and sink contracts. |
| `Agash.StreamTransport.Codecs.FFmpeg` | H.264, H.265 and AV1 encoders, decoders and a CPU processor on FFmpeg 9, FFmpeg capture devices, with natives. |
| `Agash.StreamTransport.Codecs.Opus` | Opus on Concentus. |
| `Agash.StreamTransport.Windows` | Direct3D 12 processors, Media Foundation cameras, Spout, WASAPI. |
| `Agash.StreamTransport.MacOS` | Metal processors on IOSurfaces, AVFoundation cameras, Syphon, Core Audio. |
| `Agash.StreamTransport.Linux` | Vulkan processors on DMA-BUFs, V4L2 cameras and v4l2loopback output, PipeWire video and audio. |
| `Agash.StreamTransport.Abstractions` | Rooms and signaling contracts. |
| `Agash.StreamTransport.WebRtc` | ICE, STUN, TURN, SRTP, RTP/RTCP, SDP, RTP payload formats, `PeerConnection`. |
| `Agash.StreamTransport.WebRtc.Abstractions` | Network and congestion control contracts. |
| `Agash.StreamTransport.WebRtc.CongestionControl` | The SCReAM controller. |
| `Agash.StreamTransport.WebRtc.DependencyInjection` | `AddStreamTransportWebRtc()`. |
| `Agash.StreamTransport.Signaling` | Room router and a WebSocket signaling transport. |
| `Agash.StreamTransport.Stun` | STUN binding server and ICE server providers. |

## Build

Needs the .NET 11 SDK pinned in `global.json`. Fetch the FFmpeg 9 natives for the platform first:

```bash
./eng/fetch-ffmpeg.ps1 -Rids win-x64        # or linux-x64 / linux-arm64 / osx-arm64
dotnet build StreamTransport.slnx -c Release
dotnet test  StreamTransport.slnx -c Release --filter "TestCategory!=Integration"
```

## Command line

`samples/StreamTransport.Cli` publishes and subscribes through a relay, and doubles as a test tool:

```bash
streamtransport list                                         # every input and output here
streamtransport publish --relay ws://host:8080/ws --room r --video camera --audio default
streamtransport publish ... --video test --audio test        # the measurement signal
streamtransport subscribe ... --measure                      # A/V offset and latency of it
streamtransport feed test v4l2:/dev/video42                  # a webcam showing the test signal
```

`--turn turns:host --turn-user u --turn-password p --ice-policy relay` sends only through TURN.

## Relay

A self-hostable signaling relay with a STUN server is in `samples/StreamTransport.Relay`:

```bash
dotnet run --project samples/StreamTransport.Relay     # WebSocket :8080/ws, STUN :3478
```

## License

MIT. See [LICENSE](LICENSE).
