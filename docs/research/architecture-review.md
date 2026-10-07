# StreamTransport architecture review (2026-09)

Working document for the pre-1.0 restructure. Findings first, verified against the tree at `ef6376a`
unless noted. Design proposals come after the findings are complete.

## 1. Defects found during review (fix regardless of redesign)

| # | Where | Defect | Impact |
|---|---|---|---|
| D1 | `WebRtcMediaSender.NowMicros`, `PeerConnection.Congestion.NowMicros` | `Stopwatch.GetTimestamp() * 1_000_000L / Frequency` overflows `long` once the tick count passes 9.2e12. On Linux and macOS a tick is 1 ns, so that is about 2.56 h of uptime. | Pacer and congestion controller compute garbage times on any long-running Linux/macOS host. |
| D2 | 13 clock helpers (`* (1_000_000_000L / Frequency)`) | Integer-divides before multiplying; exact only when `Frequency` divides 1e9. Also 13 copies of the same clock. | Latent skew on hosts with odd timer frequencies; no single media clock. |
| D3 | `PooledBuffer` (public, Abstractions) | Copyable struct whose `Dispose` returns its array to `ArrayPool.Shared`. Disposing two copies returns the array twice. | Silent shared-pool corruption; hard to diagnose. |
| D4 | `WebRtcMediaSender.PumpVideoAsync/PumpAudioAsync`, `PaceAsync` | Poll `TryGetFrame` / pacer budget with `Task.Delay(2)`. On Windows that waits a timer tick (~15.6 ms). | Up to a frame of added latency and jitter; wasted wakeups. |
| D5 | `avcodec_open2` call sites | The options dictionary is freed without checking for entries the encoder did not consume. | A renamed/removed vendor option is silently ignored (FFmpeg drops options between majors). |
| D6 | `MediaConfig.ResolveStun` | Blocking `Dns.GetHostAddresses` on the session start path. | Stalls a thread-pool thread on slow DNS. |
| D7 | `Directory.Packages.props` | `Microsoft.Extensions.*.Abstractions` pinned at 10.0.12 while Hosting/DI are 11.0 RC1. | Mixed-major extension stack. |
| D8 | StreamWeaver `FFmpegNativeProvisioner`, `eng/fetch-ffmpeg.ps1` | Native FFmpeg is fetched from BtbN's moving `latest` tag and loaded with no hash check. The provisioner also pins avcodec-62 and must move with the StreamTransport major. | Supply-chain exposure; "pinned" builds change underneath; two sources of truth. |
| D9 | `MediaConfig` / ICE | TURN servers provisioned and plumbed, never used (no TURN client). | CGNAT / symmetric NAT peers cannot connect. |
| D11 | Every `ffmpeg.AVERROR(ffmpeg.EAGAIN)` comparison (encoders and decoders) | FFmpeg.AutoGen hard-codes `EAGAIN = 11`; FFmpeg on macOS returns `AVERROR(35)`. The "try again" result falls through to `ThrowOnError` there. | FFmpeg-backed encode/decode on macOS throws on a normal "no output yet" or "drain first". Fixed by `FFmpeg.Interop`'s per-OS `Errno`. |
| D10 | `IVideoEncoderBackend.UpdateBitrate` | Default no-op; only `HardwareHevcEncoder` implements it. | Congestion control does nothing for the D3D11 zero-copy, VAAPI and VideoToolbox encoders. |

## 2. Public model findings

### 2.1 `VideoFrame`
- One flat `readonly record struct` carrying four mutually exclusive storages (`Surface` nint,
  `DmaBuf?`, `Pixels`, plus `SurfaceKind`). Invalid combinations are representable.
- No ownership or lifetime: the GPU handle is a borrowed `nint`; `Pixels` is `ReadOnlyMemory<byte>`
  with no release. A producer cannot say "valid until I reclaim it" (the root of #20 step 3).
- No colour description (matrix, range, primaries, transfer), no chroma siting.
- CPU frames have no plane layout: no strides, no plane offsets. Tightly packed is assumed.
- No device affinity for GPU storage; no GPU sync primitive (fence/sync-file/keyed mutex).
- No timestamp provenance (capture vs observation; ADR-115), no duration.
- `ForceKeyframe` is an encoder command riding on a frame.
- Record equality over a frame containing handles and memory is meaningless.

### 2.2 Media interfaces
- Sources are pull (`bool TryGetFrame(out T)`) and are polled; sinks are push (`void Submit(T)`).
  No async, no backpressure, no completion, no error propagation, no format negotiation.
- `IMediaTransport.CreateSender(..., nint gpuDeviceHandle)`: a raw device handle in the public API.

### 2.3 Codec model
- Only H.265 video and Opus audio are implemented (one payload format each). `VideoCodec` exposes H264
  and AV1 and the built-in profiles prefer them; negotiation only offers registered descriptors, so
  nothing false reaches the wire, but the public enum and profiles promise codecs that do not exist.
- `IVideoEncoder.Encode` returns a fresh `byte[]` per access unit (`EncodedVideoAccessUnit`).
- Encoder selection is by FFmpeg encoder-name strings, spread across selector, pipeline and options.
- `VideoEncoderSettings` / `VideoDecoderSettings` are public and carry `nint GpuDeviceHandle`,
  `EncoderName`, `PreserveAlpha`: backend concerns in the public contract.
- `IRtpPacketizer` is stateful two-step (`Packetize` then `GetPayload(i)`).

### 2.4 Options
- `MediaTransportOptions` mixes network (STUN/ICE, address prefs), codec (encoder name, B-frames,
  FEC), capture (fps hint, alpha) and playout (mode, delays) in one record.

### 2.5 WebRTC layer (in-house, ~7.2k lines + 0.5k DTLS bridge + 0.4k SCReAM)
- Implemented: ICE (host + srflx, ECN, network-change handling), SDP, RTP/RTCP incl. CCFB (RFC 8888),
  NACK/RTX, FlexFEC (RFC 8627), SRTP AES-GCM only, SCReAM congestion control, H.265 + Opus payloads.
- **No TURN client.** `IceServers` (TURN with credentials) are provisioned by the router, merged into
  options by `MediaPublisher`/`MediaSubscriber`, and then ignored: `MediaConfig` only resolves
  `StunServers`. The relay Docker image runs coturn that no client can use. Symmetric NAT / CGNAT
  (cellular IRL uplinks) cannot connect.
- No SCTP, so no data channels. Control traffic rides the signaling channel.
- DTLS 1.2 only, via BouncyCastle's blocking API on a dedicated `LongRunning` thread per handshake,
  fed through a `BlockingCollection`. Cancellation is not observed once the handshake is running.
- SRTP profile set is AES-GCM only; fine for our peers, narrower than browsers' default offer.
- No WHIP/WHEP. Interop with OBS (WHIP output since OBS 30) and browsers would need standard
  HTTP signaling and codec/profile coverage beyond H.265.

### 2.6 Platform integration
- PipeWire, Syphon, Spout sources/sinks and WASAPI audio live only in `samples/StreamTransport.Agent`.
  StreamWeaver's `Casting.Windows` carries its own copy of the Spout source/sink (~450 lines).
  ADR-112 lists "separate platform libraries" as a property to keep, but they are not packages.
- Sample sources receive frames in a callback, convert formats in managed code into reused arrays,
  keep "latest frame", and are polled. Conversion that belongs on the GPU (or in swscale) runs in C#.

## 3. FFmpeg 8.1 -> 9.0

Read from `Changelog` and `doc/APIchanges` (n8.1..n9.0) in `.refs/ffmpeg`.

- Major bump 2026-06-23 removed deprecated API. Our bindings compile unchanged; all vendor options we
  set still exist in 9.0 (checked per encoder source).
- NVENC: pre-11.1 SDK support, legacy presets (`ll`, `llhq`, ...), legacy rc modes and the `cbr`/`2pass`
  booleans removed. We already use `p4` + `tune` + `rc=cbr`.
- `AVVkFrame.access` switched to `VkAccessFlagBits2`; `AVVulkanDeviceContext.queue_flags` added.
  Relevant to our Vulkan interop (FFmpeg.AutoGen still does not bind `hwcontext_vulkan.h`).
- `SwsContext.backends` / `SwsBackend`, `SwsScaler`: swscale backend selection (new swscale graph).
- AMF hardware memory mapping; AMF HDR metadata helpers.
- `AV_CODEC_FLAG2_FIXED_FRAME_SIZE`, `AV_PKT_DATA_HEVC_CONF`, SMPTE 2094-50/APP5 HDR side data.
- FFmpeg.AutoGen 9.0.1.1 still does not bind `hwcontext_drm.h`, `hwcontext_vaapi.h`,
  `hwcontext_vulkan.h` (only D3D11VA). Our hand mirror of the DRM structs matches the 9.0 header.

TODO: evaluate which of these are worth using (Vulkan encode path, swscale backends, AMF mapping).

## 4. Precedent

Checked against source where a local copy exists (`.refs/libwebrtc`, `.refs/pipewire`,
`external/PipeWire.NET`); the rest from the projects' public APIs.

### 4.1 Frame and buffer models
- **libwebrtc** (`api/video/video_frame_buffer.h`, `video_frame.h`): a ref-counted `VideoFrameBuffer`
  with `Type` (`kNative` plus CPU layouts I420/I420A/I444/I010/NV12...). CPU fallbacks are explicit:
  `ToI420()`, `GetMappedFrameBuffer(types)`. `CropAndScale` lives on the buffer. The frame wraps the
  buffer with capture timestamp, NTP time, presentation timestamp, rotation, `ColorSpace`,
  `UpdateRect`, `RtpPacketInfos`, `is_repeat_frame`.
- **libwebrtc encoder capabilities** (`EncoderInfo`): `supports_native_handle`,
  `preferred_pixel_formats`, `requested_resolution_alignment`, `resolution_bitrate_limits`,
  `is_hardware_accelerated`, `has_trusted_rate_controller`, `implementation_name`, QP trust/limits.
  This is the capability query #20 step 4 is missing.
- **FFmpeg**: `AVFrame` with ref-counted `AVBufferRef`s; hardware frames carry `hw_frames_ctx`
  (device affinity by construction); colour properties on the frame; `av_hwframe_map` / transfer as
  explicit CPU fallbacks.
- **GStreamer**: `GstBuffer` of typed `GstMemory` (allocator decides the domain: sysmem, dmabuf, GL,
  D3D11/12, CUDA); `GstVideoMeta` carries strides/offsets; domains are negotiated through caps
  features (`memory:DMABuf`, `format=DMA_DRM`, `drm-format=NV12:0x...` since 1.24); buffer pools.
- **PipeWire / SPA** (`spa/buffer/*.h`): data types MemPtr / MemFd / DmaBuf / SyncObj; per-buffer
  `SPA_META_Header` timestamps; `SPA_META_SyncTimeline` explicit sync: an acquire point to wait on
  before reading and a release point the consumer signals when done. Ownership is expressed as a
  release fence, not as a callback lifetime.
- **PipeWire.NET 0.3** (ours): already has the shapes StreamTransport lacks. A callback-scoped
  `ref struct VideoFrame`, an allocation-free `BorrowedVideoFrame` (valid until the next cycle), an
  owning `PulledVideoFrame`/`OwnedVideoFrame`, `FrameRetention {None, Owned, Borrowed}`,
  `VideoSyncTimeline`, `VideoColorInfo`, `DrmDevice` (dev_t identity) and `DmaBufDeviceOffer`.
- **Chromium `media::VideoFrame`**: `StorageType` (owned memory, shmem, DMA-BUFs, GPU memory buffer,
  shared image), `VideoFrameLayout`, destruction observers, sync tokens.
- **WebCodecs `VideoFrame`**: explicit `close()` ownership, `copyTo(layout)`, `codedRect`,
  `visibleRect`, `colorSpace`, `timestamp`/`duration`.
- **Media Foundation / Core Video**: `IMFDXGIBuffer` (texture + subresource index), 2D buffers with
  pitch; `CVPixelBuffer` retain/release over IOSurface, `CMSampleBuffer` timing.

Common ground: (1) the storage domain is a closed set carried by the buffer, (2) ownership is explicit
(ref count, close, or release fence), (3) CPU access is an explicit conversion, never implicit,
(4) colour and layout travel with the frame, (5) device affinity is part of GPU storage.

### 4.2 Transport APIs
- **str0m** (Rust): sans-IO. The core never reads a clock or touches a socket:
  `handle_input(Timeout(now) | Receive(now, datagram))`, `poll_output() -> Timeout | Transmit | Event`.
  Deterministic tests, any runtime.
- **Pion** (Go): `MediaEngine` + `InterceptorRegistry` (NACK, TWCC, reports as a composable chain),
  `SettingEngine`, `TrackLocal`/`TrackRemote`.
- **libdatachannel** (C++): per-track media-handler chains (packetizer -> SR reporter -> NACK
  responder).
- **webrtc-rs**, **aiortc**: own DTLS/SRTP/ICE; W3C `RTCPeerConnection`-shaped APIs.
- **SIPSorcery** (.NET): event-driven, BouncyCastle DTLS, thread-per-concern. The .NET precedent to
  improve on, not copy.

## 5. Modern .NET toolbox (verified on SDK 11.0.100-rc.1)

| Feature | Verified | Use |
|---|---|---|
| C# 15 unions | `union` ships in `LangVersion=latest`. The compiler-generated form stores the case in an `object` (boxes struct cases: 24 B per construction). A custom `[Union] : IUnion` struct with its own fields and `TryGetValue(out T)` members allocates 0 B, supports implicit case conversion and pattern matching, and non-exhaustive switches raise CS8509. | Frame storage, encoder input, codec parameters: closed sets with compiler-checked dispatch. |
| Runtime async | `<Features>runtime-async=on</Features>`: no compiler state machines; publishes and runs under Native AOT. | All async pumps and the I/O driver. |
| `ref struct` + `allows ref struct` | C# 13+. | Callback-scoped borrowed frames through generic sinks. |
| `InlineArray` | In use. | Plane tables, fixed RTP header extension storage. |
| `System.Threading.Lock`, `TimeProvider` | In use / available. | One media clock, testable time. |
| `LibraryImport`, `delegate* unmanaged`, `[UnmanagedCallersOnly]`, `SuppressGCTransition` | Available. | Own FFmpeg/VAAPI/Vulkan/CoreVideo bindings without marshalling stubs. |

## 6. Dependencies

| Package | Role | Finding | Recommendation |
|---|---|---|---|
| FFmpeg.AutoGen 9.0.1.1 | FFmpeg bindings | Marshalled delegates (`GetDelegateForFunctionPointer`), not marked AOT-compatible, AOT smoke test does not cover it, no DRM/VAAPI/Vulkan hwcontext types (we hand-mirror them). | Replace with our own generated bindings (ClangSharp over the FFmpeg headers, restricted to what we call): function pointers, `SuppressGCTransition` on trivial calls, all hwcontext structs. |
| BouncyCastle.Cryptography | DTLS 1.2 | Blocking API forces a thread per handshake; cannot fit a sans-IO core; large. | Own DTLS 1.2 on BCL crypto (ECDHE P-256, ECDSA, AES-GCM, HMAC, X.509), sans-IO, with interop tests against OpenSSL and Chrome. DTLS 1.3 later. |
| Concentus | Managed Opus | AOT-friendly, no native dep. | Keep for now; benchmark against libopus (already inside the FFmpeg natives) before deciding. |
| Vortice.Direct3D11 | D3D11 COM interop | Works. | Move to CsWin32 (first-party, AOT, COM via function pointers). |
| Vortice.D3DCompiler | Runtime HLSL compile | Runtime shader compilation at startup. | Precompile at build time (DXC/FXC), embed bytecode; drop the package. |
| Vortice.Vulkan | Vulkan interop | Function-pointer based, AOT-friendly. | Keep, or generate alongside the FFmpeg bindings; decide during the Vulkan work. |
| Vortice.ShaderCompiler | Runtime GLSL->SPIR-V (native shaderc) | Ships a large native library to compile shaders at runtime. | Precompile SPIR-V at build time; drop the package. |
| NAudio (sample) | WASAPI | Sample-only. | WASAPI via CsWin32 in a Windows platform package. |
| Microsoft.Extensions.* | DI/Logging/Options/Hosting | D7. | Keep; align on 11.0. |

## 7. Proposed target architecture

### 7.1 Package map

| Package | Contents | Native deps |
|---|---|---|
| `Agash.StreamTransport.Media` | Frame model, storages, colour, time, encoder/decoder/packetizer contracts, capability types. The surface other packages and plugins build on. | none |
| `Agash.StreamTransport.WebRtc` | Sans-IO core (ICE incl. TURN, own DTLS, SRTP, RTP/RTCP, NACK/RTX, FEC, CC) plus a socket driver. BouncyCastle and the `.Dtls` package go away. | none |
| `Agash.StreamTransport.WebRtc.CongestionControl` | SCReAM (default), pluggable. | none |
| `Agash.StreamTransport.Codecs.FFmpeg` | FFmpeg-backed encoders/decoders, our generated bindings, hash-pinned native provisioning (one source of truth, replaces StreamWeaver's copy). | FFmpeg |
| `Agash.StreamTransport.Windows` | D3D11 storage and GPU conversion (precompiled HLSL), Spout (via Spout2.NET), WASAPI, Media Foundation capture. StreamWeaver deletes its Spout copy. | OS only |
| `Agash.StreamTransport.MacOS` | IOSurface storage, Metal conversion, direct VideoToolbox, Syphon (via Syphon.NET), AVFoundation, Core Audio. | OS only |
| `Agash.StreamTransport.Linux` | DMA-BUF storage, VAAPI and Vulkan (precompiled SPIR-V), PipeWire.NET 0.3 video/audio with format/modifier negotiation and explicit sync, V4L2. | OS only |
| `Agash.StreamTransport` | Composition: sessions, publisher/subscriber, rooms, DI, options. | none |
| `Agash.StreamTransport.Signaling`, `.Stun` | Same role; WHIP/WHEP added (7.6). | none |

Audio-only and relay consumers stop pulling FFmpeg; the Linux field agent takes `Media` + `WebRtc` +
`Linux` + a codec backend and nothing for other platforms.

### 7.2 Frame model

- `VideoFrame`: a `ref struct` **view**, valid for the duration of the call it is passed to; the compiler rejects storing it (section 12).
  - `Storage`: a custom non-boxing `[Union]` of `CpuImage | D3D11Image | IOSurfaceImage | DmaBufImage`
    (later `VulkanImage`, `CudaImage`). Every dispatch is an exhaustive `switch` (CS8509 as error).
  - `CpuImage`: memory plus a plane table (offset and stride per plane) in an `InlineArray`.
  - GPU cases carry their device identity (`GpuDevice`: D3D11 adapter LUID, DRM `dev_t`) and an
    optional sync primitive (D3D11 fence value or keyed mutex, DRM syncobj timeline points,
    `MTLSharedEvent`), mirroring PipeWire's explicit sync.
  - `Format` (pixel format, coded size, visible rect), `Color` (matrix, range, primaries, transfer,
    chroma siting), `Orientation`, `Timing` (7.5).
- Ownership: a view does not own. `frame.Retain()` returns a `VideoFrameLease` (pooled class,
  `IDisposable`) through a producer-supplied retainer, because only the producer knows what retaining
  costs: a CPU pool rents, a PipeWire borrowed buffer is copied on the GPU into an owned surface, an
  IOSurface is retained. Disposing the lease releases the buffer or signals the release point.
- Consumer contract: a consumer finishes reading, or calls `Retain()`, before its call returns. That
  is the whole ownership rule, and it is what lets a PipeWire DMA-BUF reach VAAPI without the copy the
  capture source makes today.
- Commands are not frame data: keyframe requests move to an `EncodeRequest` argument.

### 7.3 Delivery and negotiation

- Push, not poll: `IVideoSource.Connect(IVideoFrameConsumer)`; `OnFrame(in VideoFrame)` runs on the
  producer's thread (libwebrtc `OnFrame`, the PipeWire process callback). The encode stage chooses:
  encode inline (zero-copy, fastest) or retain and hand to a bounded encode queue with an explicit
  drop policy for live media (drop-oldest). No `Task.Delay` polling anywhere (D4).
- Negotiation before the first frame: the consumer states what it accepts (`VideoConstraints`:
  storages, formats, devices, DRM modifiers, alignment), derived from the selected encoder's
  capabilities; the source picks. For PipeWire this becomes the format params and the
  `DmaBufDeviceOffer`, so the producer allocates buffers the encoder can import directly.
- A `Channel`/`IAsyncEnumerable` adapter exists for application code that prefers pull; it is not the
  primitive.

### 7.4 Codec contracts

- `IVideoEncoder`: `VideoEncoderInfo Info` (accepted storages and formats, device, hardware flag,
  alignment, resolution limits, reconfigurable rate control, keyframe control, implementation name);
  `Encode(in VideoFrame, in EncodeRequest, IEncodedVideoConsumer)`; `Reconfigure(in RateTarget)`.
- Output is an `EncodedVideoFrame` view over encoder-owned packet memory (keyframe flag, capture time,
  codec-specific info), consumed synchronously by the packetizer, which writes RTP straight into
  pooled datagrams. No `byte[]` per access unit. `Retain()` for consumers that queue.
- Factories advertise SDP formats with fmtp (profile/level/tier) and create by format plus context.
  Selection uses a typed `VideoCodecId` and an `EncoderPreference` (hardware vendor or software),
  never FFmpeg encoder-name strings, which stay internal to the FFmpeg backend.
- Unconsumed FFmpeg options after `avcodec_open2` are an error (D5).
- Codecs: keep H.265; add H.264 (baseline interop for browsers and OBS WHIP) and AV1 (screen share,
  native alpha option). The public enum lists only what is implemented.

### 7.5 Time

One `MediaClock` (monotonic ns via `TimeProvider`, overflow-safe conversion) and a `MediaTimestamp`
carrying its kind: producer capture time or observation time (ADR-115), with producer-reported
latency kept separate. Replaces the 13 helpers (D1, D2).

### 7.6 WebRTC core and interop

- Sans-IO state machine in the str0m style: `HandleDatagram(now, ...)`, `HandleTimeout(now)`,
  `PollTransmit`, `PollEvent`, `WriteMedia`. The driver owns sockets and time. A deterministic
  in-process network simulation (loss, jitter, reorder, bandwidth) becomes the test harness for CC,
  FEC, NACK and A/V sync, and removes the timing flakiness in the relay tests.
- Own DTLS 1.2 (ECDHE-ECDSA P-256, AES-GCM, extended master secret, use_srtp, RFC 5705 exporter,
  flight retransmission) on BCL crypto; interop-tested against OpenSSL and Chrome, fuzzed at the
  record layer. DTLS 1.3 after.
- TURN client (RFC 8656: UDP, TCP, TLS) so CGNAT and symmetric NAT connect (D9).
- SRTP: add AES_CM_128_HMAC_SHA1_80 beside GCM for browser and OBS interop.
- WHIP/WHEP endpoints next to rooms, so OBS can publish straight in and browsers can watch.

### 7.7 Rockchip (field agent)

Bind Rockchip MPP directly (`mpp_create`, `MppEncCfg`, DRM-fd `MppFrame` import) as a native encoder
backend beside VideoToolbox and VAAPI, taking `DmaBufImage` storage, with RGA for conversion. This
avoids maintaining an FFmpeg 9 build with the out-of-tree rkmpp patches. Verification needs an
RK3588 board.

## 8. Staging

| Phase | Scope | Gate |
|---|---|---|
| 0 | Defects D1-D9 that survive the redesign or ship before it (D1 is live in the published alpha StreamWeaver uses). | Tests on all three rig machines. |
| 1 | `Media` package: time, frame model, storages, ownership, codec contracts, capabilities, push delivery (#20 in full). Port existing backends. | Existing suites green on Win/Linux/Mac; zero-allocation steady state measured. |
| 2 | Platform packages. PipeWire.NET 0.3 with negotiation and explicit sync, DMA-BUF zero-copy to VAAPI (#22 rest). Syphon/Spout moved in; StreamWeaver drops its copy. | Hardware verification per ADR-114. |
| 3 | Sans-IO WebRTC core, own DTLS, TURN, network-sim harness. | Interop, fuzz and sim suites. |
| 4 | Own FFmpeg bindings, precompiled shaders, CsWin32; drop FFmpeg.AutoGen, BouncyCastle, runtime shader compilers. | AOT publish of agent and field agent. |
| 5 | H.264, AV1, WHIP/WHEP, A/V sync (#14), native capture (ADR-114), rkmpp. | Per feature. |

## 9. Internal architecture: findings

Inventory of `src/Agash.StreamTransport/Codecs` (about 40 types) at `9862dde`.

### 10.1 Devices
- `VaapiDevice`, `VulkanDevice`: process-global static singletons. One creation attempt, result cached
  for the life of the process including failure, first render node only (a different `renderNode`
  argument after the first call is ignored), never released.
- The Vulkan device is created independently (`av_hwdevice_ctx_create`, default device), not derived
  from the VAAPI device. On an iGPU + dGPU machine the two can sit on different GPUs, and a DMA-BUF
  from one is not importable on the other.
- `D3D11Devices`: the device crosses layers as a raw `nint` with reference counts balanced by hand
  (two `AddRef`s "one for FFmpeg, one for the caller"). The D3D11 encoder does derive QSV from D3D11
  correctly (`av_hwdevice_ctx_create_derived`), so the right pattern exists in one place.
- No device identity (adapter LUID, DRM `dev_t`, Metal registry ID) anywhere, so no device affinity can
  be checked.

### 10.2 Codec backends
- Nine backends (4 encoders, 5 decoders) each own the whole FFmpeg lifecycle: context allocation,
  options, open, send/receive loop, PTS-to-capture-time recovery, packet copy-out. Around 2.4k lines
  with the same skeleton.
- `UpdateBitrate` is a default no-op on `IVideoEncoderBackend`; only the CPU-input `HardwareHevcEncoder`
  implements it. **Congestion control has no effect on the D3D11 zero-copy encoder (StreamWeaver's
  Spout path), VAAPI, or VideoToolbox.** On a degraded link they keep sending at the start rate. (D10)
- Capability knowledge is scattered: `HevcEncoderSelector` (name + device-type probe),
  `D3D11VideoEncoder.SupportsInputFormat` (a real test-encode probe, cached, but private to one class),
  `CodecCapabilities`, and `GpuVendorMap` (encoder name to vendor to adapter index).
- Fallback happens only at open time. A backend failing mid-stream (driver reset, session limit) ends
  the send.
- Colour description is set separately in each backend.

### 10.3 Surfaces and pools
- Each GPU API invents its own pool: a D3D11 texture array private to the encoder,
  `VaapiPresentationPool` (public, in the core package) for pre-exported DMA-BUFs, Metal and Vulkan
  resources inside their compute contexts.
- Output lifetimes live in comments only ("valid until the next `TryDecode`").
- No explicit GPU synchronisation in the model; correctness relies on each backend syncing internally.

### 10.4 Conversion
- `IAlphaPacker`, `IAlphaUnpacker`, `INv12ToBgra`, implemented per API (D3D11, Metal, Vulkan, CPU).
  They return a `VideoFrame` whose storage and lifetime are unspecified; the CPU versions allocate a
  new `byte[]` per frame.
- Nothing selects or composes them. Each capture/publish integration wires its own conversions by hand
  (in the sample), so the same decision is made in several places and differently per platform.

## 10. Internal architecture: precedent

- **FFmpeg hwcontext**: `AVHWDeviceContext` (device), `AVHWFramesContext` (typed pool: device,
  hardware format, software format, size, pool size), `av_hwdevice_ctx_create_derived` (VAAPI to
  Vulkan to DRM on one GPU, D3D11 to QSV), `av_hwframe_map` / `av_hwframe_transfer_data` for crossing
  APIs. The device, pool and interop layer we lack, and we already link it.
- **Chromium media** (`media/gpu/chromeos`, `media/video`): `VideoEncodeAccelerator` per platform with
  `GetSupportedProfiles()`; `ImageProcessor` plus a factory that picks a backend (libyuv, VAAPI VPP,
  V4L2, GL) by input and output storage and format; `DmabufVideoFramePool`/`PlatformVideoFramePool`;
  `VideoDecoderPipeline` composing decoder, image processor and frame pool, negotiating the output
  format with the consumer.
- **GStreamer**: device sharing by `GstContext` queries (one VA display or D3D11 device shared across
  elements); the ALLOCATION query lets the downstream consumer propose the buffer pool the upstream
  producer fills, which is how zero-copy is arranged rather than hoped for; element ranks for
  auto-selection.
- **libwebrtc**: `VideoEncoderFactory` / `VideoDecoderFactory` (`GetSupportedFormats`, `Create`,
  `QueryCodecSupport(format, scalability)` returning supported and power-efficient), wrapped by
  `VideoEncoderSoftwareFallbackWrapper` (runtime fallback) and `SimulcastEncoderAdapter`.
- **Media Foundation**: `IMFDXGIDeviceManager` hands one D3D device to every component; MFTs are
  enumerated with hardware flags and merit.
- **OBS**: encoders declare `OBS_ENCODER_CAP_PASS_TEXTURE` and receive shared texture handles with
  keyed-mutex keys (`encode_texture2`): zero-copy with an explicit sync primitive in the contract.

## 11. Internal architecture: proposal

The layering, bottom up. Each layer is a small set of types with one job.

1. **Devices.** `MediaDevice` is an owned, reference-counted handle (SafeHandle-based), not an `nint`,
   with an identity (`GpuIdentity`: LUID, DRM `dev_t`, Metal registry ID) and an API
   (D3D11, VAAPI, Vulkan, Metal, CUDA). A `MediaDeviceManager` service (DI singleton, not static)
   creates devices lazily per identity, retries after failure, and derives related devices on the same
   GPU (VAAPI to Vulkan to DRM, D3D11 to QSV) through FFmpeg's derivation, so a pipeline's
   components share one GPU by construction. Mirrors `IMFDXGIDeviceManager` and `GstContext`.
2. **Surface pools.** `ISurfacePool` per device and API: fixed or growable, allocate by
   (format, size, modifier, usage), handles exported once and stable (what PipeWire needs), leases
   that implement the frame retainer from 7.2. Backed by `AVHWFramesContext` where FFmpeg provides
   one. Replaces the four ad-hoc pools.
3. **Processors.** `IVideoProcessor`: one operation class per implementation (convert, scale, copy,
   alpha pack, alpha unpack), declared as capabilities over (input storage and format) to
   (output storage and format) on a device. Implementations: D3D11, Metal, Vulkan, VAAPI VPP, and CPU
   (swscale or SIMD). A `VideoProcessorFactory` picks by capability, like Chromium's
   `ImageProcessorFactory`. Output goes into a pool from layer 2, so its lifetime is a lease.
4. **Codec cores.** One `FFmpegEncoderSession` and one `FFmpegDecoderSession` own the FFmpeg lifecycle
   once: options with the unconsumed-option check, send/receive, timestamp mapping, keyframe forcing,
   bitrate reconfiguration, packet views without copies. Per-API differences shrink to small
   **input and output adapters**: CPU upload, D3D11 texture into a hardware frame, DMA-BUF mapped to
   VAAPI, D3D11 mapped to QSV; CPU readback, D3D11 output, DMA-BUF export, CVPixelBuffer output.
   Non-FFmpeg codecs (direct VideoToolbox, Rockchip MPP) implement the same encoder contract without
   the session. D10 disappears because rate control lives in one place.
5. **Factories and registry.** `IVideoEncoderFactory` / `IVideoDecoderFactory` per backend family
   (FFmpeg hardware, VideoToolbox, MPP, software), each reporting supported SDP formats and
   `QueryCapabilities(format, device)` (accepted storages and formats, hardware, power efficiency,
   limits). Registered through DI with a rank (GStreamer rank, MF merit), so a plugin or a vendor SDK
   backend is added by registering a factory. A runtime-fallback wrapper (libwebrtc style) moves to
   the next candidate when an encoder fails mid-stream and requests a keyframe.
6. **Pipeline builder.** `VideoSendPipeline` / `VideoReceivePipeline` become compositions that
   negotiate before the first frame: the chosen encoder's accepted inputs become the source's
   constraints (and its pool proposal, the GStreamer ALLOCATION idea), and a processor is inserted
   only when the source cannot produce an accepted input directly. On receive, the sink's accepted
   outputs pick the decoder output adapter and any processor. Conversions are decided once, here,
   instead of in every integration.
7. **Payload formats.** `IRtpPayloadFormat` per codec: a packetizer that writes into caller-provided
   datagram buffers and a depacketizer that assembles into pooled memory with explicit ownership,
   replacing `IRtpPacketizer`'s two-step API and `PooledBuffer` (D3).

What stays from ADR-112's list: the thin source/sink/encoder seams (now with capabilities), Spout
shared device, Syphon IOSurface, the PipeWire stream core and modifier negotiation (now driven by
layer 6), VAAPI DRM-PRIME import, D3D11 GPU conversion, the CPU fallback, timestamp-delta RTP
clocking, and source-driven cadence.

## 12. Correctness by construction

The `VideoFrame`/DMA-BUF rework happened because a lifetime rule ("valid until the producer
reclaims it") lived in comments and could not be expressed in the types. The rule for the redesign:
every ownership, lifetime, synchronisation, device and unit rule is either enforced by the compiler
or checked at runtime in tests. None is left to documentation alone.

| Rule | Mechanism |
|---|---|
| A borrowed frame cannot outlive the call that delivered it. | Borrowed `VideoFrame` and `EncodedVideoFrame` are `ref struct`s (as PipeWire.NET's callback frame already is). Storing one in a field, capturing it in a lambda or passing it across an `await` does not compile. Keeping a frame requires `Retain()`, which returns an owning lease. |
| Every lease is released exactly once. | Leases are classes (not copyable structs, unlike `PooledBuffer`, D3). Release is idempotent and thread-safe. Debug and test builds track outstanding leases per pool and fail tests on leaks and double release; use after release throws `ObjectDisposedException`. |
| A storage kind is handled everywhere it is dispatched. | Storage is a closed custom `[Union]`; every dispatch is an exhaustive `switch` (CS8509 as error). Adding a kind breaks the build at every site that must handle it. |
| A GPU surface is only consumed on its own GPU. | Storages carry `GpuIdentity`. The pipeline builder checks affinity when composing and refuses a mismatched chain at build time, not at the first frame. Devices come from `MediaDeviceManager` derivation, so a correctly built chain is on one GPU by construction. |
| A consumer never reads a surface before the producer finished writing, and the producer never reuses it before the consumer finished reading. | Sync primitives travel with the storage (DRM syncobj points, D3D11 fence value or keyed mutex, `MTLSharedEvent`). The lease's release signals the release point. Backends that cannot wait on a fence declare it in their capabilities, and the builder inserts a synchronising copy. |
| Clock domains and units are not mixed. | Distinct types: `MediaTime` (monotonic ns, one `MediaClock`), `RtpTimestamp` (wrapping `uint` with modular comparison), `NtpTime`, `ClockRate`. Conversions only through named methods that use 128-bit intermediates. D1 and D2 become compile errors. |
| Measured and observed timestamps are distinguishable. | `MediaTimestamp` carries its kind (ADR-115); producer latency is a separate field. |
| Encoder configuration is what we asked for. | Unconsumed FFmpeg options fail the open (D5). Capabilities are probed by real opens (as `D3D11VideoEncoder` does today), cached per device, and exposed. |
| Protocol state is valid. | ICE, DTLS, session states as explicit state machines with transition tables; illegal transitions throw in debug. The sans-IO core makes them unit-testable without sockets. |
| Parsers accept only valid input and never crash on invalid input. | RTP, RTCP, STUN, SDP, DTLS record/handshake parsers fuzzed (SharpFuzz) and property-tested against reference vectors. |
| Real hardware behaves as assumed. | ADR-114's validation rule becomes a matrix of named hardware tests per backend and storage, run on the rig (Windows NVIDIA/AMD, Linux AMD VAAPI/Vulkan, macOS VideoToolbox/Metal). A backend is not done until its row is green. |

## 13. Reconciliation with the open ADRs

- **ADR-108 (Accepted).** The package map in 7.1 recovers the ADR's own five modules (StreamCore,
  StreamCodec, StreamTransport, StreamSignaling, StreamInterop), which collapsed into one package.
  The 2026-06-01 update says external coturn "is supported now"; it is not (D9). This review proposes
  a TURN client only, consistent with that update; hosting a TURN server stays deferred as the body
  says. Phase 3 allows a browser sender and Phase 2 names MediaMTX/OBS WebRTC ingest, which is what
  motivates WHIP/WHEP, H.264 and AES-CM (decision 4). The SRT/SRTLA future transport requires the
  frame, codec and encoded-frame model to be independent of WebRTC, which the `Media` package gives.
  The decided `AvatarTransparent` profile was never added. The Layer 0 table (Spout2.NET "not built")
  is stale.
- **ADR-112 (Proposed).** Steps 1, 2 and 4 landed as a `VideoSurfaceKind` enum; this review replaces
  that with the storage union and completes steps 3, 4 (capability query), 5 and 6. Its
  "must not lose" list is honoured (section 11, end). Its warning that half-owned handles are worse
  than copies is why leases are all-or-nothing and why a producer's retainer decides between
  holding and copying.
- **ADR-114 (Proposed).** Native sources (V4L2, Media Foundation, AVFoundation) plug into the storage
  union unchanged; the proposal adds MPP direct binding for Rockchip in place of an rkmpp FFmpeg
  build. Its validation rule becomes the hardware matrix in section 12.
- **ADR-115 (Proposed).** `MediaTimestamp` with its kind and separate latency is exactly its
  decision; section 12 makes the clock domains type-distinct so conversions cannot be mixed up.

## 14. Decisions needed

1. Package split as in 7.1 (breaking for every consumer; greenfield).
2. Replace BouncyCastle with our own DTLS (security-sensitive; mitigated by interop and fuzz tests).
3. Replace FFmpeg.AutoGen with generated bindings.
4. Interop goals: browsers and OBS via WHIP/WHEP, which pulls in H.264 and AES-CM SRTP.
5. rkmpp through direct MPP bindings, and whether to get an RK3588 board for verification.
