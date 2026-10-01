# Agash.StreamTransport.MacOS

macOS media for [Agash.StreamTransport](https://github.com/Agash/StreamTransport): Metal video
processors on IOSurfaces, Syphon, and Core Audio.

Requires macOS 15 or later with a Metal GPU, and .NET 11 with the `macos` workload: the package targets
`net11.0-macos`. On other systems the project builds empty, so a solution that includes it builds
everywhere.

## Metal video processors

`MetalVideoProcessorFactory` implements `IVideoProcessorFactory` with compute kernels on the GPU the
frames are on:

- BGRA to NV12, at any even size, in the BT.601, BT.709 or BT.2020 matrix and either range.
- BGRA with alpha to a side-by-side NV12 frame twice as wide: colour on the left, alpha as luma on the
  right with neutral chroma, so transparency passes through any 4:2:0 encoder. The layout is the one
  every StreamTransport platform writes and reads.
- NV12 to BGRA, and a side-by-side frame back to one with alpha.

Frames arrive and leave as `IOSurfaceImage`s. The processor waits for an input's shared event when it
has one, and its output frames leave finished: VideoToolbox and Syphon clients read IOSurfaces directly
and cannot wait on a Metal event. Output surfaces come from a Core Video pixel-buffer pool, laid out as
VideoToolbox expects, and return to it when the last lease on their frame is released.

Metal compiles the kernels for each device the first time a processor, source or sink uses it.

## Syphon

`SyphonVideoSource` receives a Syphon server as 8-bit BGRA IOSurfaces, zero-copy while a consumer's call
lasts; a consumer that keeps a frame gets a GPU copy, since Syphon has no lock and the server renders its
next frame into the same surface. One client serves every connected consumer. Syphon carries no capture
time, so frames are stamped with when they were observed.

`SyphonVideoSink` publishes 8-bit BGRA frames as a Syphon server: IOSurfaces on its GPU, copied into the
server's surface on the GPU, or frames in memory. A processor makes BGRA from what a decoder produces.
OBS, Resolume and every other Syphon application see the server.

Find servers with Syphon.NET's `SyphonServerDirectory`:

```csharp
using SyphonServerDirectory directory = new();
SyphonServerDescription server = await directory.WaitForServerAsync(s => s.AppName == "VTube Studio");
using SyphonVideoSource source = new(server);
```

The directory hears servers through the main run loop, which an AppKit application runs already; a
console host runs its work in `SyphonMainLoop.Run`.

## AVFoundation

`AVFoundationVideoInputProvider` lists built-in, external and Continuity cameras and camera extensions
(virtual cameras), with their uncompressed modes (NV12, YUYV, UYVY, BGRA). Each frame is the camera's
IOSurface-backed buffer: an IOSurface for a consumer on the GPU, its mapped memory otherwise. Timestamps
are AVFoundation's capture times on the host clock, which is the media clock. macOS asks the user before
an application sees a camera; an application states why in `NSCameraUsageDescription`.

## Core Audio

`CoreAudioSource` captures the default input device and `CoreAudioSink` plays on the default output,
both through AVAudioEngine at 48 kHz stereo float, converted to and from the device's format; a mono
microphone is heard on both channels. Both follow the default device when it changes. Captured audio
carries Core Audio's host time for its first sample, on the media clock. `CoreAudioSink.OutputLatency`
reports how long audio takes to reach the speaker, for a session's audio output offset.

macOS asks the user before an application hears the microphone, and delivers silence until they allow
it; an application states why in `NSMicrophoneUsageDescription`.

## Registration

`AddMacOSMedia()` registers the processor, which sessions then choose for IOSurface frames, and the
AVFoundation, Syphon and Core Audio providers, reached through `MediaDevices`:

```csharp
services.AddStreamTransport().AddFFmpegCodecs().AddMacOSMedia();
```

