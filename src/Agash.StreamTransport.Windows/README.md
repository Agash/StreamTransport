# Agash.StreamTransport.Windows

Windows media for [Agash.StreamTransport](https://github.com/Agash/StreamTransport).

## Direct3D 12 video processors

`D3D12VideoProcessorFactory` implements `IVideoProcessorFactory` with compute shaders on the GPU the
frames are on:

- BGRA or RGBA to NV12, at any even size, in the BT.601, BT.709 or BT.2020 matrix and either range.
- BGRA or RGBA with alpha to a side-by-side NV12 frame twice as wide: colour on the left, alpha as luma
  on the right with neutral chroma, so transparency passes through any 4:2:0 encoder.
- NV12 to BGRA or RGBA, and a side-by-side frame back to one with alpha.

Frames arrive and leave as `D3D12Image`s in `COMMON`. The processor waits for the input's fence or
queue, and each output frame carries a fence its consumer waits on. Output textures come from a pool and
return to it when the last lease on their frame is released.

The shaders ship as signed DXIL; building on Windows with the SDK's `dxc` recompiles them from
`Shaders/*.hlsl`.

## Spout

`SpoutVideoSource` receives a Spout sender as Direct3D 12 textures, zero-copy while a consumer's call
lasts; a consumer that keeps a frame gets a GPU copy. One receiver serves every connected consumer. Spout
carries no capture time, so frames are stamped with when they were observed.

`SpoutVideoSink` publishes 8-bit BGRA or RGBA Direct3D 12 textures on its GPU as a Spout sender; a
processor makes those from what a decoder produces.

Both run on Spout2.NET's native Direct3D 12 sharing, on a direct queue of their own.

## Registration

Register the processor with a codec registry like any processor:

```csharp
services.TryAddEnumerable(ServiceDescriptor.Singleton<IVideoProcessorFactory, D3D12VideoProcessorFactory>());
```
