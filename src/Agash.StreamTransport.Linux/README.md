# Agash.StreamTransport.Linux

Linux media for [Agash.StreamTransport](https://github.com/Agash/StreamTransport): V4L2 cameras and
v4l2loopback webcams, PipeWire video and audio, and Vulkan video processors on DMA-BUFs.

Requires a running PipeWire daemon (1.0 or later, with a session manager such as WirePlumber) and, for
the GPU path, a Vulkan 1.2 driver with DMA-BUF import and export (`VK_EXT_external_memory_dma_buf`,
`VK_EXT_image_drm_format_modifier`), which Mesa's RADV and ANV and NVIDIA's driver have.

## GPU first

Video shared between applications stays on the GPU. A PipeWire source whose consumer takes DMA-BUFs
offers the producer (OBS, VTube Studio, a compositor's screen capture) to share its buffers on the
source's GPU, in the layouts the GPU can import; the Vulkan processor converts them in place for the
encoder; a decoder's DMA-BUFs are converted back and published by a PipeWire sink on buffers shared
with its consumer. Cameras, which hand over frames in memory, and producers that cannot share, fall
back to memory.

## Vulkan video processors

`VulkanVideoProcessorFactory` implements `IVideoProcessorFactory` with compute kernels on the GPU the
frames are on:

- BGRA or RGBA to NV12, at any even size, in the BT.601, BT.709 or BT.2020 matrix and either range.
- BGRA or RGBA with alpha to a side-by-side NV12 frame twice as wide: colour on the left, alpha as luma
  on the right with neutral chroma, so transparency passes through any 4:2:0 encoder. The layout is the
  one every StreamTransport platform writes and reads.
- NV12 to BGRA or RGBA, and a side-by-side frame back to one with alpha.

Input DMA-BUFs are imported plane by plane; the processor waits for their writers (implicit fences)
before reading. Output pictures are DMA-BUFs in rows, one per plane, from a pool; they leave finished
and return to the pool when the last lease on their frame is released. The kernels ship as SPIR-V,
recompiled by `glslc` when a build finds it.

## V4L2

`V4l2VideoInputProvider` lists every `/dev/video*` node that streams captured video, single- or
multi-planar, with the uncompressed modes the media model has (YUYV, UYVY, NV12, I420, BGRA, RGBA):
webcams, USB capture cards, HDMI receivers. Frames are the driver's buffers, lent without a copy: as
DMA-BUFs to a consumer on a GPU (the driver is asked for rows aligned as GPUs import them, and buffers are
lent only when it gives them), as mapped memory otherwise. A consumer that keeps a frame keeps its buffer.
Timestamps are the driver's capture times on the monotonic clock. A camera that sends only MJPEG is left
to the FFmpeg provider.

`V4l2VideoOutputProvider` writes received video to a v4l2loopback device, named by path or card name,
which video calls, browsers and OBS open as a webcam.

## PipeWire

`PipeWireVideoSource` captures a node, by id or name, or the session manager's choice. Frames on
shared buffers are the producer's own while a consumer's call lasts; a consumer that keeps one gets a
GPU copy. Frames in memory (BGRA, RGBA, NV12, I420) are read in place, every plane, whether the
producer put the planes in separate blocks or one. Timestamps are the producer's when they are on the
monotonic clock, otherwise the graph cycle the frame was queued in.

`PipeWireVideoSink` publishes a node consumers see as a camera, declaring its colour. DMA-BUF frames
go out on shared buffers, each filled with one GPU copy from the latest frame; frames in memory are
staged once and copied into the daemon's buffer.

`PipeWireAudioSource` and `PipeWireAudioSink` capture and play 48 kHz stereo float, converted by the
graph; captured audio is stamped with its capture time from the stream's delay, and
`PipeWireAudioSink.OutputLatency` reports how long audio takes to reach the device.

The PipeWire providers share one `PipeWireConnection`, a connection to the daemon and its graph started
on first use; an application making sources and sinks itself hands them a `PipeWireContext`. Video nodes
are listed as inputs, the cameras PipeWire serves among them, keyed by their device node so a camera the
V4L2 provider also lists appears once.

## Registration

`AddLinuxMedia()` registers the processor, which sessions then choose for DMA-BUF frames, and the V4L2 and
PipeWire providers, reached through `MediaDevices`:

```csharp
services.AddStreamTransport().AddFFmpegCodecs().AddLinuxMedia();
```
