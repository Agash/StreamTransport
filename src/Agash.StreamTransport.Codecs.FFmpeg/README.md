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

`FFmpegVideoProcessorFactory` converts frames in system memory (pixel format, size, colour) on
swscale. It ranks below the GPU processors of the platform packages and serves as the fallback.

Modern APIs rank first where there is a choice: Direct3D 12 ahead of the Direct3D 11 vendor paths,
Vulkan ahead of VA-API. Every backend takes system memory as well.

## Behaviour

- Each factory opens a real encoder or decoder once per codec and GPU and pushes frames through it.
  A backend whose driver lacks the codec, whose GPU is missing, or that falls back to software
  reports no capabilities.
- Encoders run with no reordering or lookahead, and constant bit rate under a rate-control buffer
  sized for the tuning (interactive, screen content, loss resilient). FFmpeg rejects an option an
  encoder does not know, so a renamed option fails the open.
- Congestion control reaches every backend. NVENC and Quick Sync take a rate change between frames;
  the other encoders reopen at the next frame, starting with a keyframe, once the target has moved
  far enough from their rate to be worth it (15% down or 30% up).
- Every backend honours keyframe requests. An encoder that ignores a forced picture type is reopened
  for the keyframe.
- Decoded frames are views over FFmpeg's buffers or surfaces. Retaining one takes a reference to them.
- The stream signals the configuration's colour, or the first frame's, or BT.709 when neither gives one.
- FFmpeg's own log goes to the container's logging, under `FFmpeg.*` categories, for as long as the
  container lives (`FFmpegCodecOptions.RouteFFmpegLog` turns this off). What FFmpeg logs while a
  backend is probed is demoted to Debug; the probe's outcome is logged by the factory.

## Diagnostics

The meter `Agash.StreamTransport.Codecs.FFmpeg` (`FFmpegCodecDiagnostics.MeterName`), from the host's
`IMeterFactory` with DI, tagged with `streamtransport.ffmpeg.codec`:

| Instrument | Unit | What |
|---|---|---|
| `streamtransport.ffmpeg.encoder.opens` | `{open}` | encoders opened, by `streamtransport.reason`: `start`, `rate`, `frame_rate`, `keyframe`, `storage`; every open after the start costs a keyframe |
| `streamtransport.ffmpeg.encoder.open.duration` | `s` | time to open an encoder |
| `streamtransport.ffmpeg.probes` | `{probe}` | encoders and decoders tried at first use, by `streamtransport.outcome`: `works` or `unavailable` |

## Capture devices

`FFmpegVideoInputProvider` reaches cameras through FFmpeg's capture devices (`dshow`, `v4l2`,
`avfoundation`). It ranks below the platform providers: a camera they list is opened by them, and by
FFmpeg when they cannot open it. It decodes cameras that send only MJPEG, and reaches DirectShow-only
virtual cameras on Windows.

## Native libraries

The package carries the FFmpeg 9 shared libraries per runtime (`runtimes/<rid>/native`) and FFmpeg's
licence text. Windows and Linux use BtbN's LGPL builds, pinned and verified by SHA-256. They include
the hardware encoders and the LGPL software codecs. The GPL x264 and x265 are left out.
