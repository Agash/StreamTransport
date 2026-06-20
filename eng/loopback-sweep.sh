#!/usr/bin/env bash
# Self-loopback sweep: sender + verifying receiver on THIS machine (relay local), so the only thing removed
# vs the cross-machine matrix is the wire. It isolates the platform's full pipeline - encode -> our WebRTC ->
# decode -> GPU publish - from cross-machine network/CC, per resolution x fps x profile. Run on each platform
# (mac/linux/windows-gitbash). HOST defaults to 127.0.0.1; pass the LAN IP to force the path over the NIC.
#
# Usage: bash loopback-sweep.sh [host] [publish: auto|none] [profiles] [seconds]
set -u
HOST="${1:-127.0.0.1}"
PUBLISH="${2:-auto}"          # auto = platform GPU publish (syphon/pipewire/spout); none = CPU decode
PROFILES="${3:-interactive screenshare irl}"
SECS="${4:-15}"
PORT=8099
export PATH="$HOME/.dotnet:/opt/homebrew/bin:/usr/local/bin:$PATH" DOTNET_ROOT="$HOME/.dotnet"

OS=$(uname -s)
PUBFLAG=()
case "$OS" in
  Darwin)
    REPO="$HOME/repos/StreamTransport"
    AGENT=$(find "$REPO/samples/StreamTransport.Agent/bin/Release" -path '*osx-arm64*' -name streamtransport-agent -type f 2>/dev/null | head -1)
    run_agent() { "$AGENT" "$@"; }
    [ "$PUBLISH" = auto ] && PUBFLAG=(--publish-syphon LB) ;;
  Linux)
    REPO="$HOME/stx"
    AGENT="$REPO/samples/StreamTransport.Agent/bin/Release/net11.0/streamtransport-agent.dll"
    run_agent() { dotnet "$AGENT" "$@"; }
    export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/1000}" WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}"
    [ "$PUBLISH" = auto ] && PUBFLAG=(--publish-pipewire LB) ;;
  *) # MINGW/MSYS (Windows Git Bash)
    REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
    AGENT="$REPO/samples/StreamTransport.Agent/bin/Release/net11.0-windows10.0.19041.0/streamtransport-agent.exe"
    run_agent() { "$AGENT" "$@"; }
    PUBFLAG=() ;; # Windows self-loopback uses CPU decode (Spout self-publish needs a consumer)
esac
[ "$PUBLISH" = none ] && PUBFLAG=()
RELAY="$REPO/samples/StreamTransport.Relay/bin/Release/net11.0/StreamTransport.Relay.dll"
[ -n "${AGENT:-}" ] && [ -e "$AGENT" ] || { echo "agent not found: ${AGENT:-?}"; exit 97; }

STREAMTRANSPORT_RELAY_URLS="http://0.0.0.0:$PORT" dotnet "$RELAY" >/tmp/lb-relay.out 2>&1 &
RELAY_PID=$!
for i in $(seq 1 30); do curl -s --max-time 1 "http://localhost:$PORT/health" >/dev/null 2>&1 && break; sleep 0.5; done

echo "== self-loopback on $OS  host=$HOST  publish=${PUBFLAG[*]:-none}  secs=$SECS =="
printf "%-9s %-9s %-4s %-7s %-7s %s\n" "profile" "res" "fps" "recv" "pub" "verdict"
for prof in $PROFILES; do
  for res in 1280x720 1920x1080 2560x1440 3840x2160; do
    for fps in 30 60; do
      room="lb$(echo "${res}${fps}${prof}" | tr -d 'x')"
      run_agent send --relay "ws://$HOST:$PORT/ws" --room "$room" --source synthetic --video-only --verify \
        --resolution "$res" --fps "$fps" --profile "$prof" --seconds $((SECS + 8)) >/tmp/lb-send.log 2>&1 &
      SP=$!; sleep 2
      run_agent receive --relay "ws://$HOST:$PORT/ws" --room "$room" --source synthetic --video-only --verify \
        --seconds "$SECS" --profile "$prof" "${PUBFLAG[@]}" >/tmp/lb-recv.log 2>&1
      kill "$SP" 2>/dev/null
      recv=$(grep -oE 'video : [0-9]+ frames \(([0-9.]+) fps\)' /tmp/lb-recv.log | grep -oE '[0-9.]+ fps' | head -1)
      pub=$(grep -oE 'publish: [0-9]+ frames in [0-9.]+s \([0-9.]+ fps\)' /tmp/lb-recv.log | grep -oE '[0-9.]+ fps\)$' | tr -d ')' | head -1)
      v=$(grep -oE 'VERIFY-(PASS|FAIL)' /tmp/lb-recv.log | head -1)
      printf "%-9s %-9s %-4s %-7s %-7s %s\n" "$prof" "$res" "$fps" "${recv:-0fps}" "${pub:--}" "${v:-NONE}"
    done
  done
done
kill "$RELAY_PID" 2>/dev/null
echo "done"
