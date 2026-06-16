# StreamTransport Agent

A cross-platform command-line sender/receiver for `Agash.StreamTransport`. It captures video (a Spout, Syphon,
or PipeWire shared surface, a camera, or a synthetic test pattern) plus audio, encodes hardware H.265 and Opus,
moves them peer-to-peer over WebRTC, and on the far side decodes and publishes back into a Spout or Syphon
surface that OBS can pick up. It is both the reference wiring for the library and the end-to-end test harness.

```
send agent:     capture -> H.265/Opus encode -> WebRTC --------\
                                                                 > relay (signaling only)
receive agent:  publish <- H.265/Opus decode  <- WebRTC --------/
```

## Contents

- [Prerequisites](#prerequisites)
- [FFmpeg: providing the right build](#ffmpeg-providing-the-right-build)
- [Quick start](#quick-start)
- [Selecting video and audio](#selecting-video-and-audio)
- [Capturing from OBS (Spout / Syphon / PipeWire)](#capturing-from-obs-spout--syphon--pipewire)
- [Publishing back into OBS](#publishing-back-into-obs)
- [Media profiles](#media-profiles)
- [Connectivity options](#connectivity-options)
- [Verifying a link](#verifying-a-link)
- [Full option reference](#full-option-reference)

## Prerequisites

- The .NET 11 preview SDK to run from source, or a published single-file binary from the GitHub release.
- An FFmpeg 8.1 shared build (see the next section). Audio-only runs do not need it.
- For GPU capture/publish: Spout (Windows), Syphon (macOS), or PipeWire (Linux). Camera and synthetic sources
  need none of these.

## FFmpeg: providing the right build

The agent binds to **FFmpeg 8.1** (the `avcodec-62` ABI) through `FFmpeg.AutoGen`. A different major version
(7.x is `avcodec-61`, a future 9.x is `avcodec-63`) will not load. This matters on machines that already have
some other FFmpeg installed by unrelated software (a media player, a previous toolchain): you must make the
agent pick up the correct build rather than the wrong global one.

### Where the agent looks (in order)

The loader probes these locations and uses the first that contains an `avcodec` library, so a local copy always
wins over a system install:

1. `runtimes/<rid>/native/` next to the executable (how the published binary ships its natives).
2. The executable's own directory (a flat single-RID layout).
3. A `native/ffmpeg/<rid>/` folder found by walking up from the executable (the dev layout).
4. Only if none of the above exist: the operating system's default library search path (Windows `PATH`, Linux
   `ldconfig` / `LD_LIBRARY_PATH`, macOS `dyld`). This is the fallback that can pick up a system FFmpeg, and the
   only path where a wrong global version could be loaded.

`<rid>` is the runtime identifier: `win-x64`, `linux-x64`, `linux-arm64`, or `osx-arm64`.

### Overriding a wrong global FFmpeg

Because locations 1 to 3 are checked before the system path, drop the correct 8.1 shared libraries into one of
them and the agent ignores whatever is installed globally. The simplest is a `native/ffmpeg/<rid>/` folder next
to the agent, or directly beside the executable.

**Windows / Linux** - fetch the pinned build with the repo script (writes into `native/ffmpeg/<rid>/`):

```bash
./eng/fetch-ffmpeg.ps1 -Rids win-x64        # or linux-x64 / linux-arm64
```

Or copy the shared libraries yourself next to the executable: `avcodec-62.dll`, `avformat-62.dll`,
`avutil-60.dll`, `swscale-9.dll`, `swresample-6.dll` on Windows; the matching `libav*.so.*` on Linux.

**macOS** - install via Homebrew (its build is VideoToolbox-enabled) and make sure it is the 8.x series, then
either rely on the system path or, to be explicit, symlink the libraries next to the agent:

```bash
brew install ffmpeg
mkdir -p native/ffmpeg/osx-arm64
ln -sf "$(brew --prefix ffmpeg)"/lib/*.dylib native/ffmpeg/osx-arm64/
```

The published macOS binary does not bundle FFmpeg, so a Homebrew (or hand-placed) 8.x build is required there.
The Windows and Linux release binaries bundle the correct natives next to the executable, so they already
override any global install.

## Quick start

Start a relay (signaling only; no media flows through it), then a sender and a receiver pointed at the same
room:

```bash
dotnet run --project samples/StreamTransport.Relay                                   # ws://localhost:8080/ws + STUN :3478
dotnet run --project samples/StreamTransport.Agent -- send    --relay ws://localhost:8080/ws --room demo
dotnet run --project samples/StreamTransport.Agent -- receive --relay ws://localhost:8080/ws --room demo
```

With no `--source`, the sender emits an animated test pattern and a tone, so this works with no capture
hardware. Running the published binary, replace `dotnet run --project ... --` with `./streamtransport-agent`.

## Selecting video and audio

By default the sender captures video and audio. Restrict or redirect with:

- `--source synthetic|camera|spout|syphon|pipewire` - the video source kind.
- `--video-device <name>` - a camera (implies `--source camera`). Windows uses the DirectShow device name
  (for example `Integrated Camera`), macOS an AVFoundation index (`0`), Linux a V4L2 path (`/dev/video0`).
- `--audio-device <name>` - a microphone for the camera path. Omit it for video-only camera capture.
- `--audio-only` / `--video-only` - send a single track.

List camera and microphone device names with the platform's own tools (`ffmpeg -list_devices true -f dshow -i
dummy` on Windows, `ffmpeg -f avfoundation -list_devices true -i ""` on macOS, `v4l2-ctl --list-devices` on
Linux).

## Capturing from OBS (Spout / Syphon / PipeWire)

The agent captures another application's shared GPU surface zero-copy and encodes it directly. To send what OBS
is showing, have OBS publish a shared surface and point the agent at it.

### Windows: Spout

1. Install the [OBS Spout plugin](https://github.com/Off-World-Live/obs-spout2-plugin) (recent OBS builds
   include a Spout output).
2. In OBS, enable a Spout output / source filter; note its sender name (for example `OBS`).
3. Run the sender against it:

   ```bash
   streamtransport-agent send --relay ws://<relay>/ws --room demo --spout-sender OBS
   ```

   `--spout-sender <name>` implies `--source spout`. Omit the name to take the first available sender.

### macOS: Syphon

1. Install a Syphon output for OBS (for example the Syphon output plugin), or use any Syphon-emitting app.
2. Run the sender:

   ```bash
   streamtransport-agent send --relay ws://<relay>/ws --room demo --syphon-server "OBS Source"
   ```

   `--syphon-server <name>` implies `--source syphon`. Omit the name to take the first advertised server.

### Linux: PipeWire

```bash
streamtransport-agent send --relay ws://<relay>/ws --room demo --source pipewire
```

The PipeWire dmabuf capture path and the VAAPI HEVC encoder are hardware-verified on AMD (radeonsi). The
opaque GPU zero-copy publish (`--publish-pipewire`, below) is gst-loopback verified end to end; the alpha
(transparency) zero-copy path on Linux is still in progress (tracked as issue #5).

## Publishing back into OBS

The receiver decodes into a GPU surface and republishes it under a name OBS can capture, so the remote feed
appears as a normal source.

- **Windows:** `--publish-spout <name>` republishes as a Spout sender. Add a Spout2 source in OBS with that
  name.

  ```bash
  streamtransport-agent receive --relay ws://<relay>/ws --room demo --publish-spout StreamTransport
  ```

- **macOS:** `--publish-syphon <name>` republishes as a Syphon server. Add a Syphon client source in OBS with
  that name.

  ```bash
  streamtransport-agent receive --relay ws://<relay>/ws --room demo --publish-syphon StreamTransport
  ```

- **Linux:** `--publish-pipewire <name>` republishes as a PipeWire video node (GPU dmabuf zero-copy when the
  decoder is VAAPI, CPU-converted fallback otherwise). Audio is published to a companion `<name> Audio` node.
  Add a PipeWire/Screen-capture source in OBS targeting that node.

  ```bash
  streamtransport-agent receive --relay ws://<relay>/ws --room demo --publish-pipewire StreamTransport
  ```

Without a `--publish-*` flag the receiver just decodes and reports frame stats (useful for a quick check).
Transparency is preserved end to end when the sender runs with `--alpha` and the surface carries an alpha
channel.

## Media profiles

`--profile <name>` picks a preset that trades latency against resilience:

- `interactive` (default) - two-way, low-latency, present-on-arrival; for a LAN or a good link.
- `screenshare` - efficient codecs, latency-tolerant; for desktop/screen content.
- `irl` - one-way field contribution over a lossy cellular uplink: deeper jitter buffer, FlexFEC, aggressive
  congestion adaptation, and the mobility layer.

## Connectivity options

The sender and receiver find each other through a signaling channel; media is always peer-to-peer.

- `--relay <ws-url>` - connect to a relay's WebSocket endpoint (both sides use the same `--room`).
- `--host` - the sender hosts signaling itself on the LAN (no separate relay).
- `--devtunnel` - the sender hosts signaling and exposes it over a DevTunnel for an over-the-internet link
  (uses the authenticated `devtunnel` CLI).
- `--room <code>` - the room to join. A sender generates one if omitted; a receiver must supply it.

For connectivity across NATs, configure STUN/TURN on the relay; the bundled relay also runs a STUN server, and
the Docker image adds coturn for TURN.

## Verifying a link

`--verify` runs a fixed measurement window and prints a content + audio/video sync report, then exits, so it is
scriptable for an automated end-to-end check:

```bash
streamtransport-agent send    --relay ws://<relay>/ws --room demo --profile irl --verify --seconds 20
streamtransport-agent receive --relay ws://<relay>/ws --room demo --profile irl --verify --seconds 18
```

A `VERIFY-PASS` line means video flowed, audio flowed, and (on a same-machine run) the two were in sync. The
`selftest` command runs a self-contained GPU round-trip with no relay: `selftest` on macOS (Syphon +
VideoToolbox) and Windows (Spout), and `selftest alpha [encoder]` for the side-by-side alpha path.

### Cross-machine verify matrix

`eng/verify-matrix.ps1` orchestrates a full cross-machine matrix from a Windows host: it starts the relay
locally, then runs each cell as a sender on one machine and a verifying receiver on the other (over SSH for the
Linux leg, via `eng/matrix-linux-agent.sh`), exercising CPU/GPU × video / video+audio / `--synced` / `--alpha`
in both directions. Both sides use the synthetic source with `--verify --seconds N`, so each self-terminates and
the receiver's `VERIFY-PASS`/`VERIFY-FAIL` is the per-cell gate (graded per cell: lip-sync is only required for
`--synced` cells). Media is direct P2P (WebRTC ICE); only signaling crosses the relay.

```powershell
pwsh eng/verify-matrix.ps1 -Build           # build both sides, then run all cells
pwsh eng/verify-matrix.ps1 -Cells C3,C5      # run specific cells
```

Note: the in-agent **GPU-readback** verify currently exists only on the Linux receiver (`--publish-pipewire`,
which taps each decoded GPU surface); the Windows receiver's `--verify` is CPU-decode. Adding a GPU-output verify
tap on Windows (D3D11/Spout) and macOS (Metal/Syphon), plus the macOS leg, is what turns this 2-way harness into
the full 3-way matrix — see issues #6 and #7.

## Full option reference

| Option | Meaning |
|---|---|
| `send` / `receive` / `selftest` | The mode (first argument). |
| `--relay <ws-url>` | Relay WebSocket URL to connect to. |
| `--room <code>` | Room code (required for `receive`). |
| `--host` | Host signaling on this machine (LAN), sender only. |
| `--devtunnel` | Host signaling and share it over a DevTunnel, sender only. |
| `--source <kind>` | `synthetic`, `camera`, `spout`, `syphon`, or `pipewire`. |
| `--video-device <name>` | Camera device; implies `--source camera`. |
| `--audio-device <name>` | Microphone device for the camera path. |
| `--spout-sender <name>` | Spout sender to capture; implies `--source spout`. |
| `--syphon-server <name>` | Syphon server to capture; implies `--source syphon`. |
| `--publish-spout <name>` | Republish the received video as a Spout sender (Windows). |
| `--publish-syphon <name>` | Republish the received video as a Syphon server (macOS). |
| `--publish-pipewire <name>` | Republish the received video (+ audio) as a PipeWire node (Linux). |
| `--encoder <name>` | Force a specific FFmpeg encoder (for example `hevc_vaapi`). |
| `--audio-only` / `--video-only` | Send a single track. |
| `--alpha` | Preserve transparency via side-by-side packing. |
| `--profile <name>` | `interactive`, `screenshare`, or `irl`. |
| `--bframes <n>` | Override the profile's B-frame count. |
| `--synced` | Force capture-clock-synced playout on the receiver. |
| `--verify` | Run a fixed window and print a content + sync report. |
| `--seconds <n>` | The `--verify` window length. |
| `--verbose` | Debug-level logging. |
