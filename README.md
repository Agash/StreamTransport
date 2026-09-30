# StreamTransport

[![NuGet](https://img.shields.io/nuget/v/Agash.StreamTransport.svg)](https://www.nuget.org/packages/Agash.StreamTransport)
[![CI](https://github.com/Agash/StreamTransport/actions/workflows/ci.yml/badge.svg)](https://github.com/Agash/StreamTransport/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Peer-to-peer real-time media for .NET over WebRTC: video in H.264, H.265 or AV1 and Opus audio between
machines, on a first-party WebRTC stack (ICE, STUN, DTLS-SRTP, RTP/RTCP, SDP) built on the BCL and
NativeAOT-friendly.

```
source (camera, screen, Spout, Syphon, PipeWire) -> encoder -> WebRTC -> decoder -> sink
```

> ### Alpha
> The APIs are still changing. The core is exercised by tests on Windows, Linux and macOS; try it and file
> issues, and keep it out of production for now.

## What you get

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
  NACK/RTX and FlexFEC, Opus in-band FEC and concealment, and ICE restart and hot-standby mobility.
- **Lip sync**: capture times travel as abs-capture-time; synced playout holds audio and video in one
  adaptive buffer and releases them by capture time.
- **Profiles**: `InteractiveP2P`, `ScreenShare`, `IrlContribution` and `AvatarTransparent` set codec
  preference, rate control, repair and playout in one choice. `AvatarTransparent` carries alpha side by
  side through an opaque codec.

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

Sources implement `IVideoSource`/`IAudioSource` and push frames; sinks implement `IVideoSink`/`IAudioSink`
and say what they accept.

## Packages

| Package | What it is |
|---|---|
| `Agash.StreamTransport` | Sessions, streams, pacing, sync, rooms, codec registry, DI. |
| `Agash.StreamTransport.Media` | Frames, storages, time, and the codec, processor, source and sink contracts. |
| `Agash.StreamTransport.Codecs.FFmpeg` | H.264, H.265 and AV1 encoders, decoders and a CPU processor on FFmpeg 9, with natives. |
| `Agash.StreamTransport.Codecs.Opus` | Opus on Concentus. |
| `Agash.StreamTransport.Windows` | Direct3D 12 processors, Spout, WASAPI. |
| `Agash.StreamTransport.MacOS` | Metal processors on IOSurfaces, Syphon, Core Audio. |
| `Agash.StreamTransport.Linux` | Vulkan processors on DMA-BUFs, PipeWire video and audio. |
| `Agash.StreamTransport.Abstractions` | Rooms and signaling contracts. |
| `Agash.StreamTransport.WebRtc` | ICE, STUN, SRTP, RTP/RTCP, SDP, RTP payload formats, `PeerConnection`. |
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

## Relay

A self-hostable signaling relay with a STUN server is in `samples/StreamTransport.Relay`:

```bash
dotnet run --project samples/StreamTransport.Relay     # WebSocket :8080/ws, STUN :3478
```

## License

MIT. See [LICENSE](LICENSE).
