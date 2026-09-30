# Agash.StreamTransport.Codecs.FFmpeg

H.264, H.265 and AV1 video encoders and decoders for
[Agash.StreamTransport](https://github.com/Agash/StreamTransport), on FFmpeg 9 through
[FFmpeg.Interop](https://github.com/Agash/FFmpeg.Interop). They implement the `IVideoEncoderFactory`
and `IVideoDecoderFactory` contracts of `Agash.StreamTransport.Media`, so a pipeline asks each factory
what it can do on which GPU and picks the best.

## Backends

| Encoders | Takes without a copy | Platforms |
|---|---|---|
| `D3D12` (Direct3D 12 Video Encode, any vendor) | Direct3D 12 textures | Windows |
| `Nvenc`, `Amf` | Direct3D 11 textures (copied on the GPU into the encoder's surfaces) | Windows; NVENC also Linux |
| `Qsv` | system memory | Windows, Linux |
| `Vulkan` (Vulkan Video) | system memory | Windows, Linux |
| `Vaapi` | DMA-BUFs | Linux |
| `VideoToolbox` | IOSurfaces | macOS |
| `MediaFoundation` | system memory | Windows |
| `Software` (OpenH264, kvazaar, SVT-AV1) | system memory | all |

| Decoders | Produces | Platforms |
|---|---|---|
| `D3D12`, `D3D11` | Direct3D 12 or 11 textures | Windows |
| `Vaapi` | DMA-BUFs | Linux |
| `Vulkan` | system memory | Windows, Linux |
| `VideoToolbox` | IOSurfaces | macOS |
| `Software` (libavcodec, dav1d) | system memory | all |

Modern APIs rank first where there is a choice: Direct3D 12 ahead of the Direct3D 11 vendor paths,
Vulkan ahead of VA-API. Every backend takes system memory as well.

## Behaviour

- Each factory opens a real encoder or decoder once per codec and GPU and pushes frames through it.
  A backend whose driver lacks the codec, whose GPU is missing, or that falls back to software
  reports no capabilities.
- Encoders run with no reordering or lookahead, and constant bit rate under a rate-control buffer
  sized for the tuning (interactive, screen content, loss resilient). FFmpeg rejects an option an
  encoder does not know, so a renamed option fails the open.
- Congestion control reaches every backend. NVENC takes a rate change between frames; the other
  encoders reopen at the next frame, starting with a keyframe.
- Every backend honours keyframe requests. An encoder that ignores a forced picture type is reopened
  for the keyframe.
- Decoded frames are views over FFmpeg's buffers or surfaces. Retaining one takes a reference to them.
- The stream signals the configuration's colour, or the first frame's, or BT.709 when neither gives one.

## Native libraries

The package carries the FFmpeg 9 shared libraries per runtime (`runtimes/<rid>/native`) and FFmpeg's
licence text. Windows and Linux use BtbN's LGPL builds, pinned and verified by SHA-256. They include
the hardware encoders and the LGPL software codecs. The GPL x264 and x265 are left out.
