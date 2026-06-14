# Linux GPU zero-copy pipeline — grounded plan

Authoritative, reference-backed plan for the Linux GPU path (VAAPI/Vulkan/PipeWire). Grounded the same way
as the WebRTC work (libwebrtc + RFCs): exact-version sources vendored in `.refs/` and read directly, never
guessed from web search.

## Goal / end state

In every use case we output **GPU-ready, as-zero-copy-as-possible** video to a sharing sink so an **OBS**
instance picks it up: **Spout2** (Windows, D3D11 shared texture), **Syphon** (macOS, IOSurface), **PipeWire**
(Linux, DMA-BUF). On **Linux, PipeWire is also the audio sink**; on Windows/macOS audio is separate (NAudio
etc.) and handled later.

## Reference implementations vendored in `.refs/` (read these, do not web-search)

- **`.refs/ffmpeg`** — FFmpeg `release/8.1`, **libavutil 60.26 — exact match to the handheld** (`libavutil.so.60.26.101`,
  `n8.1.1`). The ABI source of truth for the structs FFmpeg.AutoGen does NOT expose:
  - `libavutil/hwcontext_vulkan.h` — `AVVulkanDeviceContext`, `AVVkFrame`, `AVVulkanFramesContext`, `AVVulkanDeviceQueueFamily`.
  - `libavutil/hwcontext_drm.h` — `AVDRM{Frame,Object,Layer,Plane}Descriptor` (already used by the VAAPI dmabuf code).
  - `libavutil/hwcontext_vaapi.h`, `hwcontext_d3d11va.h` — device contexts for the unification idea.
  - `libavutil/hwcontext.c` — `av_hwframe_map` dispatch (grounded the encoder import fix).
  - `libavutil/hwcontext_vulkan.c` — Vulkan frames alloc / map / dmabuf export internals.
- **`.refs/mpv`** — orchestration playbook for Linux: `video/out/hwdec/hwdec_vulkan.c` (FFmpeg Vulkan device
  wrap + `lock_frame`/`unlock_frame` + per-plane `VkImage` access + timeline-semaphore sync), `hwdec_vaapi.c`,
  `hwdec_drmprime.c`; PipeWire audio in `audio/out/ao_pipewire.c`.
- **`.refs/libwebrtc`** — WebRTC transport reference (sparse; no PipeWire/dmabuf here).
- **`.refs/PipeWire.NET`** — our owned bindings (dmabuf/modifier negotiation we added).

### ABI gotcha (cost me a wrong `qf` read — write it the version-correct way)
At **libavutil 60**, `AVVulkanDeviceContext` still contains the **deprecated** `FF_API_VULKAN_FIXED_QUEUES`
fields (`queue_family_index, nb_graphics_queues, queue_family_tx_index, nb_tx_queues, queue_family_comp_index,
nb_comp_queues, queue_family_encode_index, nb_encode_queues, queue_family_decode_index, nb_decode_queues` = 10
ints) **and** `FF_API_VULKAN_SYNC_QUEUES` (`lock_queue`, `unlock_queue` = 2 fn ptrs, 16 bytes) **before**
`qf[64]`/`nb_qf`. Total 56 bytes the master-branch header (major ≥61) omits. We read only the *leading* handle
fields (`alloc, get_proc_addr, inst, phys_dev, act_dev` @ 0/8/16/24/32), which precede all of this — so
`VulkanDevice` does NOT need the full struct. The compute queue family is found by querying the physical
device directly via Vortice (not by reading `qf`).

## Architecture (symmetric per platform; user-confirmed)

Per platform: **{GPU surface, codec interop, alpha pack/unpack compute}**.

| | GPU surface | codec | alpha compute | sink (OBS) |
|---|---|---|---|---|
| Windows | D3D11 texture | D3D11VA / NVENC | `D3D11AlphaPacker` (D3D11 CS) | Spout2 |
| macOS | IOSurface / CVPixelBuffer | VideoToolbox | Metal (`SyphonAlphaCodec`) | Syphon |
| Linux | **dmabuf** | VAAPI (+ Vulkan decode) | **Vulkan compute** | PipeWire |

- **Common (non-alpha) Linux path uses NO Vulkan**: dmabuf end-to-end (PipeWire ↔ VAAPI), mirroring the
  single-surface flow on Win/Mac. Encoder dmabuf import (DONE) + decoder dmabuf export (DONE) are the halves.
- **Vulkan is ONLY the Linux alpha engine** (peer of `D3D11AlphaPacker`/Metal). dmabuf is the surface
  interchange; FFmpeg bridges dmabuf↔Vulkan↔VAAPI.
- **PipeWire republish pool = GPU images we own** (exportable Vulkan images for alpha; VAAPI surfaces for
  non-alpha), dmabuf-exported ONCE at `add_buffer` (`vkGetMemoryFdKHR`). Each frame the final stage writes
  into the current pool buffer. Stable fds → fits the `ALLOC_BUFFERS`/`add_buffer` producer we built, no
  per-frame dmabuf churn. PipeWire still needs dmabuf (can't take a `VkImage`) but it's *exported from* our
  Vulkan image, not round-tripped.
- **Vulkan sync**: use FFmpeg's `AVVulkanFramesContext.lock_frame`/`unlock_frame` callbacks (mpv
  `hwdec_vulkan.c:263,300`) + transition via `AVVkFrame.sem`/`layout`/`sem_value` (timeline semaphores). Don't
  invent sync. Vortice provides the Vulkan API/types only; hand-define the FFmpeg `AVVk*` structs from
  `.refs/ffmpeg/libavutil/hwcontext_vulkan.h`.

## Status
- DONE+HW-verified: VAAPI decode→dmabuf export; dmabuf→VAAPI encode import; FFmpeg Vulkan device + decode
  HEVC→`VkImage`; PipeWire.NET dmabuf/modifier (released 0.2.0-alpha).
- See `~/.claude/.../memory/zero-copy-dmabuf-plan.md` for the live status + remaining-work checklist.

## Remaining implementation (grounded in the above)
1. **Agent Vulkan compute on `AVVkFrame`** (alpha unpack): rework `VulkanComputeContext` to borrow
   `VulkanDevice.{Instance,PhysicalDevice,Device}` (Vortice `vkInitialize` + `GetApi`; query phys-dev for a
   compute queue family; `vkGetDeviceQueue`). Hand-define full `AVVkFrame` + `AVVulkanFramesContext`. Run the
   alpha `.spv` on `img[]` with `lock_frame`/`unlock_frame` + sem/layout transitions.
2. **Capture→encode wiring**: `PipeWireVideoCaptureSource` build `DmaBufSurface` from PipeWire planes+modifier
   (derive per-plane DRM fourcc from SPA format); push/synchronous (use fd within `FrameReady`).
3. **Republish sink**: `PipeWireVideoOutput.ConnectDmaBuf` pool = owned exportable images, final stage writes in.
4. Alpha pack (encode) over a captured dmabuf imported to Vulkan (FFmpeg map drm→vulkan).
5. 3-way verify (Mac → handheld + Windows).
