# StreamTransport 1.0 readiness audit (2026-09-28)

Git-excluded working document. Every claim below was checked against code, CI logs or a hardware run
on the date given; "claimed" means stated in an ADR, issue, README or earlier report.

## 1. Ledger of claims

| Claim | Source | Verdict | Evidence |
|---|---|---|---|
| FFmpeg.Interop covers encode, decode and every hwcontext StreamTransport uses | ADR-118 §4 | Holds, with gaps G1-G2 below | Every `ffmpeg.*` call in StreamTransport `src/` and `samples/` mapped to a managed API; VAAPI to DRM PRIME pool-less `MapTo` tested on the lab box |
| Zero-copy D3D12 texture import | FFmpeg.Interop release notes | Holds | `WrapD3D12Texture` tests on RTX 3060: encode PSNR, fence ordering, byte-exact readback |
| PipeWire.NET 0.3 has the frame model StreamTransport lacks (ref struct frame, borrowed/owned, sync timeline, DRM device, device offer) | architecture review §4.1 | Types exist; behaviour on hardware being verified | CI never runs the GPU path: all 54 `RequiresGpu` tests are Inconclusive on hosted runners; GStreamer end-to-end leg fails 3 of 88 and is non-gating. Lab box run in progress |
| Syphon.NET is byte-exact in-process, cross-process and against syphon-python | layer0 memory, ADR-108 | Holds in CI | 6 transport tests + 2 interop runs pass on the macOS runner with Metal. The Mac has no Xcode, so the `net11.0-macos` test host cannot be built there |
| Spout2.NET byte-exact D3D11 round trip | layer0 memory | Holds, but flaky and thin | 2 tests. 1 failure in 7 local runs (first run after a build). The receiver hands out the sender's shared texture with no access mutex held |
| Spout2.NET "not built" | ADR-108 Layer 0 table | Stale | Published to alpha.3 |
| ADR-112 step 3 (ownership), 5 (VAAPI wiring), 6 (publish side) | ADR-112 status table | Not started, as stated | Carried into ADR-118 frame model |
| StreamTransport #22 (PipeWire.NET 0.3, FFmpeg 9) | issue | Open, superseded in part | FFmpeg side moves to FFmpeg.Interop instead of FFmpeg.AutoGen 9 |

## 2. FFmpeg.Interop gaps for StreamTransport

- G1. `IOSurface` to `CVPixelBuffer` wrap for the VideoToolbox encoder (`CVPixelBufferCreateWithIOSurface`).
  Today the caller must build the pixel buffer; Syphon and AVFoundation hand over IOSurfaces.
- G2. libavfilter graphs (`scale_vaapi`, `scale_vulkan`, `scale_d3d11`, `scale_vt`, `hwmap`): the
  FFmpeg-native GPU conversion path. StreamTransport currently drives libva VPP by hand. Decide
  per ADR-118 §3 whether processors use filters or platform shaders.

## 3. Layer 0 parity target

PipeWire.NET is the reference; Spout and Syphon match it as far as GPU texture sharing allows.

| Capability | PipeWire.NET | Spout2.NET target | Syphon.NET target |
|---|---|---|---|
| Native layer | ClangSharp-generated | ClangSharp over the shim header, drift-checked | Same |
| Native object ownership | SafeHandles | SafeHandles | SafeHandles |
| Callback-scoped frame | `ref struct VideoFrame` | `ref struct SpoutFrame`, access mutex held for its lifetime | `ref struct SyphonFrame` |
| Keeping a frame | `Clone()` / owned frame | `Retain()`: GPU copy into a receiver-owned texture | `Retain()`: IOSurface retain + use count |
| Delivery | push (process callback) + pull | push (frame-sync event, else waitable timer at sender fps) + pull | push (Syphon new-frame handler) + pull |
| Producer sync | explicit sync timeline | Spout access mutex / keyed mutex, taken by the view | IOSurface use count |
| Device identity | `DrmDevice` | adapter LUID of receiver device and sender adapter | Metal registry ID |
| Colour | `VideoColorInfo` | from DXGI format (UNORM sRGB, 16F scRGB linear) | sRGB BGRA |
| Timing | graph time, delay, PTS | frame number, sender fps, observed time (marked as observation) | frame number, observed time |
| Discovery | registry with events | sender directory with change events (polled; Spout has no notification) | directory with announce/retire/update events |
| Publish | output stream, DMA-BUF export | `Send(texture)` GPU copy; zero-copy `AcquireTexture` + `Publish` into the shared texture | `Publish(IOSurface)` zero-copy; `AcquireSurface` + `PublishCurrent` |
| Metadata | SPA meta header | Spout memory buffer (per-sender) | none in Syphon |
| Language / platform | C# 14, net10+net11 | C# 15, net11, CsWin32 for D3D11 | C# 15, net11.0-macos |

## 4. Decision: no native shims (2026-09-28)

Spout2.NET and Syphon.NET follow FFmpeg.Interop and PipeWire.NET: the native layer is only generated
bindings (CsWin32 for Win32/D3D11, ClangSharp for C headers) plus hand-written `LibraryImport`; all logic
is C# in the managed layer. The Spout C++ shim is dropped: Spout2.NET implements the Spout protocol in C#.
The vendored Spout SDK stays as the reference and as an independent interop peer for tests only. Syphon is
next: evaluate reimplementing its protocol over CoreFoundation C APIs (CFMessagePort, distributed
notifications, IOSurfaceLookup) instead of the Objective-C shim.

## 5. Spout protocol (read from SDK 2.007.017 sources)

- Shared memory primitive: `CreateFileMappingA(name, size)` / `OpenFileMappingA`, guarded by mutex
  `<name>_mutex`, lock wait 67 ms.
- Sender registry map `SpoutSenderNames`: MaxSenders x 256 bytes (MaxSenders = 64, overridable by
  `HKCU\Software\Leading Edge\Spout\MaxSenders`). Consecutive NUL-terminated 256-byte slots, sorted by
  byte order (std::set<std::string>), terminated by an empty slot. Registering a sender sets it active.
  Stale names (no per-sender map) are removed by readers.
- Active sender map `ActiveSenderName`: 256 bytes, the name.
- Per-sender map `<name>`: SharedTextureInfo, 280 bytes: u32 shareHandle, width, height, format, usage,
  u8 description[256] (sender exe path), u32 partnerId (0x80000000 CPU sender, 0x40000000 GL/DX).
  shareHandle 0 means a CPU (memory-share) sender. The map existing is the liveness test.
- Texture: D3D11 texture with legacy shared handle (MISC_SHARED), handle stored as 32 bits.
  Receivers `OpenSharedResource` on their own device; fails across adapters.
- Access: named mutex `<name>_SpoutAccessMutex` (wait 67 ms), or the texture's IDXGIKeyedMutex (key 0,
  67 ms) when created with MISC_SHARED_KEYEDMUTEX.
- Frame count: semaphore `<name>_Count_Semaphore` (initial 1, max LONG_MAX). Sender per frame: wait 0 then
  ReleaseSemaphore(2) = +1. Reader peeks: wait 0 then ReleaseSemaphore(1, &previous). Count 0 = sender
  does not count. Frame counting is off unless registry `Framecount` = 1 in the SDK; we always count.
- Frame sync: auto-reset event `<name>_Sync_Event`; wakes one waiter, so 1:1 only. Push delivery for many
  receivers must poll the count semaphore.
- Metadata: map `<name>_map`, first 16 bytes reserved for the size (read SpoutDX Write/ReadMemoryBuffer
  for the exact header before implementing).
- Fps: sender-side smoothed estimate (0.95/0.05 EMA) from frame intervals; receivers estimate from counts.
- Still to read before implementing: SpoutDirectX CreateSharedDX11Texture (bind/misc flags), memory
  buffer header, 2.006 memoryshare detection.
