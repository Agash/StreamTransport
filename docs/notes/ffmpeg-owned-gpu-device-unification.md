# Idea: unify GPU interop on "FFmpeg owns the device, we borrow its native handle"

**Status:** idea / future refactor. Not blocking the current zero-copy featureset. Written down so it isn't forgotten.

## The pattern (proven on Linux/Vulkan)

Instead of creating our own GPU device and importing FFmpeg's frames into it (the fragile, per-driver
dmabuf/modifier path), let **FFmpeg create the hardware device** (`av_hwdevice_ctx_create`) — it enables
exactly the extensions/features its decode + interop paths need — and **borrow the native device handle** out
of FFmpeg's hw device context for our own GPU work (capture sharing, alpha pack/unpack compute, encode).

This is the mpv model (`hwdec_vulkan.c`), inverted: mpv wraps an existing libplacebo device *into* FFmpeg; we
don't have libplacebo, so we let FFmpeg own the device and read its handles. Verified on Linux: `VulkanDevice`
reads `AVVulkanDeviceContext.{inst,phys_dev,act_dev}` and decode-to-`AV_PIX_FMT_VULKAN` yields `AVVkFrame`
`VkImage`s our compute can run on — no `VK_EXT_image_drm_format_modifier`, no `vkGetMemoryFdPropertiesKHR`.

## Does it generalize across backends?

| Backend | FFmpeg device type | Native handle exposed | Borrowable shared device? |
|---|---|---|---|
| Linux VAAPI | `AV_HWDEVICE_TYPE_VAAPI` | `AVVAAPIDeviceContext.display` (VADisplay) | **Yes** |
| Linux Vulkan | `AV_HWDEVICE_TYPE_VULKAN` | `AVVulkanDeviceContext.{inst,phys_dev,act_dev}` | **Yes (verified)** |
| Windows D3D11 | `AV_HWDEVICE_TYPE_D3D11VA` | `AVD3D11VADeviceContext.{device,device_context}` (`ID3D11Device*`) | **Yes** |
| macOS VideoToolbox | `AV_HWDEVICE_TYPE_VIDEOTOOLBOX` | none — frames are per-frame `CVPixelBuffer` (IOSurface-backed); FFmpeg has **no** Metal device type | **No** — borrow the per-frame IOSurface instead |

So the "FFmpeg owns the device, borrow the handle" pattern unifies cleanly on **Linux (VAAPI + Vulkan) and
Windows (D3D11)**. **macOS is the exception:** there is no FFmpeg-created Metal device to borrow; VideoToolbox
hands out `CVPixelBuffer`/IOSurface per frame, which is exactly what Syphon consumes. The macOS path is the
"borrow the per-frame surface" variant of the same idea (FFmpeg owns the *decode*, we borrow each output
surface) rather than "borrow one shared device".

## Why it's worth doing later

Today we have several distinct patterns: D3D11 creates its own device (`D3D11Devices`); the retired Linux
Vulkan path created its own device + hand-rolled dmabuf import; VAAPI shares one process-wide `VADisplay`
(`VaapiDevice`). Adopting "FFmpeg owns the device, we borrow the handle" on Linux + Windows would collapse
those into one shape: a per-backend `XxxDevice` that calls `av_hwdevice_ctx_create` and exposes the native
handle, consumed identically by capture-share and compute code. macOS stays on the per-frame-IOSurface model.

## Caveats / open questions for when we do it
- Windows: confirm Spout can share an `ID3D11Device` created by FFmpeg (it should — it's a normal device),
  and that we can pick the right adapter (FFmpeg's D3D11 device-create takes an adapter index).
- Borrowed-handle lifetime: the device lives as long as the FFmpeg `AVBufferRef` device context — keep one
  process-wide ref (as `VaapiDevice`/`VulkanDevice` already do).
- Queue/command-pool discovery (Vulkan): query the physical device for a compute family and `vkGetDeviceQueue`
  on a family FFmpeg created a queue for (the universal graphics+compute family on desktop GPUs is safe).
