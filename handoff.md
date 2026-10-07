# Handoff (2026-10-07)

Work in progress on the StreamTransport 1.0 restructure (StreamWeaver ADR-118, issues #20, #22, #26).
Branch: `restructure`. Everything below is pushed.

## Read first

1. `docs/research/restructure-ledger.md`: the working ledger. Every item (T, F, Z, A, C rows) with its
   status and evidence, the step plan, the research notes and the state at handoff at the end.
2. The rules below. They are not negotiable and apply to everything that lands in or near a repository.

## Rules

**Attribution:** never attribute any work to an AI, an assistant, Claude or Anthropic, under any
circumstances. That covers:
- `Co-Authored-By` lines and session trailers or links;
- "generated with" lines;
- commit messages, PR titles and bodies, issues, branch and tag names;
- code comments, file headers and docs.

Write everything as the human author would.

**Writing.** Plain, concise, developer-to-developer.
- No filler, no marketing tone, no summaries of what a diff already shows.
- No em or en dashes, and no "X, not Y" contrasts.
- No history: describe what the code is, never what it replaced or "no longer" does. That applies to
  code, comments, docs and commit messages alike.

**Commits.** Conventional Commits. Subject only, 50 characters or fewer, imperative. Add a one- or two-line
body only when it explains a non-obvious why. One logical change per commit.

**Code.**
- **References:** no ADR, audit or ledger ids in code comments; only GitHub issue and PR numbers.
- **Warnings:** fix every warning at source.
- **Timing:** through `TimeProvider`, never `Task.Delay` in library code.
- **Logging:** `[LoggerMessage]` with disjoint event ids.
- **Copies:** zero-copy and in-place wherever the platform allows.
- **Pre-release:** no compatibility shims.

**Repository.** Do not commit `docs/references/`: it holds copies of third-party RFCs, drafts and
sources. Never dispatch CI workflows on `main` of the published libraries, because dispatching publishes
to NuGet. Releases and upstream patches are the owner's call.

## State

- **Done and pushed today:**
  - 1804a4a: encoder ranking (encoders that change rate in place first, after GPU input). The native
    D3D12/Vulkan/VideoToolbox codec packages are tracked in #26.
  - bc0b4e0: per-frame latency tracing on one clock; about 50 ms end to end on Windows loopback with
    NVENC, lip sync within 0.1 ms.
  - d9847da, bc6c746, fae5647, 7a75175: media mobility (T25 to T28).
  - 932bb1a, 07ea78d: WHIP/WHEP trickle and ICE restart over PATCH (T29).
  - 6fe0796: FlexFEC negotiated in SDP per RFC 8627 (part of T14).
  - d8879b8: side-by-side alpha refuses odd colour widths (A1).
  - T15: the joint recovery policy (`RecoveryPolicy` in Adaptation), sizing FlexFEC groups from loss,
    round trip and frame size, with retransmission always on.
  - T32: retransmissions and FEC repairs travel over the standby pair on the other interface (second
    modem) when `RepairOverSecondPath` is on (IRL profile), outside the media path's congestion control.
- **Tests:** Windows, every suite passes. Mac, every suite passed at b038056. The Linux lab box has been
  unreachable since bc0b4e0 (it sleeps), so it has not run anything after that.
- **Decided:**
  - RTX is kept next to FlexFEC parity (libwebrtc practice). This deviates from RFC 8627 section 1.1.7 and
    is documented in `PeerConnection.Fec.cs`.
  - RFC naming only, no `flexfec-03` alias.
  - Rate-changing encoders rank first until #26 lands.
  - IPv6 first for P2P; TURN is supported but never the answer.

## Next, in order

1. Run the lab box suite: `ssh agash@192.168.20.102`, fish shell, start the suite with
   `systemd-run --user`. A cloud session cannot reach the LAN machines (lab box and Mac at
   192.168.20.183), so cross-machine runs wait for the local machine.
2. **Alpha and colour, from two external reviews:**
   - A3 to A10 in the ledger, with verification marks (A1 and A2 are fixed). A5 and A9 are confirmed in
     code; the rest are still to verify.
   - In particular, VideoToolbox hardware decode of HEVC alpha (A9).
   - Also `x-alpha` negotiated from real encoder and decoder capabilities (A10), and `AlphaLayout.Layer`
     made codec-neutral (HEVC now; VVC and AV2 later).
3. **C1 codec research:** VVC (RFC 9328, FFmpeg 9's vvc decoder and libvvenc, realtime viability,
   AUX_ALPHA), AV1 complete, AV2 preparation.
4. **The rest of the ledger:**
   - T16 network-emulation rig (lab box, netem).
   - Zero-copy items Z9, Z10, Z19, Z23, Z30.
   - ARM codec engines Z26 (field agent).
   - Follow-ups F1 to F22.
   - End review and ADRs.
   - G1 Casting move, H1 releases (user's call), I1 merge.

## Machines and references

- **Windows** (this repo's primary box): `dotnet build StreamTransport.slnx`, then run each
  `tests/*/bin/Debug/net11.0*/*.Tests.exe`.
- **Mac:** `$HOME/.dotnet/dotnet`, Xcode-beta through `DEVELOPER_DIR`, `~/st-sync-suite-mac.sh`.
- **Lab box:** `~/st-sync-suite-lab.sh`, run under `systemd-run --user` because the box kills user
  processes.
- **`docs/references/`:** re-fetch it when absent. It holds the RFCs (9000, 9221, 8445, 7675, 8838, 8863,
  9725, 8840, 8627, 8899, 8985, 8285), drafts (quic-multipath-21, quic-address-discovery-01,
  seemann-quic-nat-traversal-02, avtcore-rtp-over-quic-14, masque-connect-udp-listen-16,
  moq-transport-22, webtrans-http3-16, thatcher-ice-renomination-01, screamv2) and libwebrtc sources
  (ice/, timing/, fec/). They come from rfc-editor.org, ietf.org/archive/id and
  webrtc.googlesource.com (`?format=TEXT`, base64).
