# Output-boundary A/V sync: measure + compensate the differential publish/output latency (#14)

**Status:** measurement + reporting landed and verified; compensation mechanism landed but **default-off**
pending real-output verification. Tracks StreamTransport issue #14.

## The problem

The `--verify` A/V sync check (and the `PlayoutScheduler` it validates) aligns audio and video at the
**decode / scheduler-release** stage — the part the receiver controls — and now does so consistently on every
platform (#12, #31). But that is **not** the eyes-and-ears skew the viewer/OBS experiences:

- The **audio** marker is timed when the scheduler releases audio to the measurement sink. In `--verify` audio
  bypasses the real device, so the audio device buffer latency (WASAPI is configured to 50 ms; CoreAudio /
  PipeWire output nodes run a ~20 ms quantum) is never in the loop.
- The **video** marker is timed at decode (pre-publish), so the publish-staging latency (VAAPI VPP → dmabuf →
  PipeWire / Metal convert → Syphon queue) is excluded.

So the measured skew ≈ scheduler alignment, while the real output skew is `Lv − La` (the differential
downstream latency), which the receiver neither measured nor compensated.

## Model

For a matched capture instant the scheduler releases audio and video together (`videoRelease == audioRelease`).
Downstream, video reaches the viewer at `videoRelease + Lv` and audio at `audioRelease + La`. Lip-sync at the
**output** requires `videoRelease + Lv == audioRelease + La`, i.e. shift the audio release by `Lv − La`:

```
audioRelease' = audioRelease + (Lv − La)   ⟹   audioRelease' + La = videoRelease + Lv   (synced at output)
```

`Lv − La` may be negative (audio is the slower path — the common case, since the audio device buffer dwarfs
the video publish-staging cost), which releases audio *earlier*. Video is the reference (offset 0): on the GPU
path it presents on arrival and cannot be held, and on the CPU path keeping video fixed means only one stream
moves.

## What landed

**Measurement + reporting (issue steps 1–2) — verified, always on:**
- The GPU publish sinks (`PipeWireVideoPublishSink`, `SyphonVideoPublishSink`) measure `Lv = publish hand-off −
  decode PresentationTimeNs` per frame (before the verify readback, so its cost is excluded) and report it via
  `VerificationReport.RecordVideoPublishLatency`. The CPU verify path has no publish sink, so `Lv ≈ 0`.
- `La` is supplied per receiver platform (Run.cs): Windows 50 ms (WASAPI), Linux/macOS ~20 ms (node quantum).
- `--verify` prints an informational **output-boundary skew** line: `scheduler skew + (Lv − La)`, with the
  per-path `Lv`/`La` kept in the report. The IN/OUT-SYNC **verdict stays on the controllable scheduler skew**,
  so this adds observability without changing pass/fail.
  - Verified Linux GPU: `Lv ~4 ms, La ~20 ms` → boundary `−27 ms` (scheduler `−11`).
  - Verified Windows CPU: `Lv ~0 ms, La ~50 ms` → boundary `−28 ms` (scheduler `+22`).

**Compensation mechanism (issue step 3) — landed, default-off:**
- `PlayoutScheduler.Schedule` / `ScheduleOnTimeline` take an `extraDelayNs`; `WebRtcMediaReceiver` applies it to
  the **audio** release only (`Lv − La`), via `SetOutputLatencyOffset(long)` on `IMediaReceiver` /
  `MediaSubscriber`. At the default offset 0 the behaviour is byte-for-byte the pre-#14 scheduler-aligned
  playout (12/12 `PlayoutScheduler` tests pass unchanged).

## Why compensation is default-off

Turning it on changes a core sync default. Two things must be settled first, and both need real-output
measurement the agent can't observe internally:

1. **`La` must be device-queried, not assumed.** WASAPI is configured (50 ms), but the CoreAudio/PipeWire
   values are representative quanta, not measured device buffer depth. A wrong `La` *de*-syncs the real output.
2. **The boundary skew is partly external.** Downstream of the publish/device hand-off (OBS composite, display
   vsync, speaker) is not directly observable, so compensation must be validated against an actual capture of
   the rendered output (e.g. an OBS recording with a clap/flash test), not just the in-agent estimate.

Enabling it: query each platform's real device latency, then have the host call
`subscriber.SetOutputLatencyOffset(Lv − La)` once the measurements settle, and verify against a recorded output
that the rendered lip-sync improved. Until then the mechanism ships inert and the report exposes the gap.
