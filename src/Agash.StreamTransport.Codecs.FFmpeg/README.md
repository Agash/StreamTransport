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

- **Probed, not assumed.** Whether a backend works on a GPU is found out once by opening a real
  encoder or decoder and pushing frames through it. A driver without the codec, a missing GPU or a
  silent software fallback reports no capabilities instead of failing on a live stream.
- **Real-time settings** per encoder: no reordering or lookahead, constant bit rate under a
  rate-control buffer sized for the tuning (interactive, screen content, loss resilient). FFmpeg
  rejects an option an encoder does not know, so a renamed option fails loudly.
- **Congestion control reaches every backend.** Encoders that take a rate change while running get it
  between frames; the others reopen at the next frame with a keyframe.
- **Keyframe requests** are honoured on every backend; one that ignores a forced picture type is
  reopened for the keyframe.
- **Decoded frames are not copied.** They are views over FFmpeg's buffers or surfaces, and retaining
  one takes a reference.
- **Colour** is signalled in the stream: the configuration's, else the first frame's, else BT.709.

## Native libraries

The package carries the FFmpeg 9 shared libraries per runtime (`runtimes/<rid>/native`): BtbN's LGPL
builds for Windows and Linux, pinned and verified by SHA-256, and FFmpeg's licence text. They include
the hardware encoders and the LGPL software codecs, not the GPL x264 and x265.
