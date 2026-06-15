#!/usr/bin/env bash
# Linux GPU zero-copy PipeWire publish loopback verification (headless-capable).
#
# relay + sender(--alpha? synthetic) -> WebRTC -> receiver(--publish-pipewire [--alpha], GPU dmabuf) -> PipeWire
# node, consumed by gst. We use `pipewiresrc ! fakesink` (no glupload): fakesink accepts dmabuf memory directly,
# so it prerolls headless over SSH (glupload needs a GL/EGL context that isn't available without a display).
# PASS = the receiver does NOT crash AND gst pulls the requested buffers (exit 0). GL/EGL import correctness for
# OBS is a separate, display-attached check.
#
# Env toggles: ALPHA=1|0 (default 1), VERBOSE=1|0 (default 0), PORT (default 8099).
set -u

ROOT="$HOME/stx"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/1000}"
export WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}"
export STX_PW_DEBUG=1
PORT="${PORT:-8099}"
export STREAMTRANSPORT_RELAY_URLS="http://0.0.0.0:$PORT"
WS="ws://localhost:$PORT/ws"
ALPHA="${ALPHA:-1}"
VERBOSE="${VERBOSE:-0}"
if [ "$ALPHA" = "1" ]; then AFLAG="--alpha"; NODE="AlphaVerify"; else AFLAG=""; NODE="OpaqueVerify"; fi
if [ "$VERBOSE" = "1" ]; then VFLAG="--verbose"; else VFLAG=""; fi
AGENT="$ROOT/samples/StreamTransport.Agent/bin/Release/net11.0/streamtransport-agent.dll"
RELAY="$ROOT/samples/StreamTransport.Relay/bin/Release/net11.0/StreamTransport.Relay.dll"
LOG="$ROOT/.alpha-verify"
mkdir -p "$LOG"; rm -f "$LOG"/*.log
cd "$ROOT" || exit 99

pids=()
cleanup() { for p in "${pids[@]:-}"; do kill "$p" 2>/dev/null; done; }
trap cleanup EXIT

[ -f "$RELAY" ] || { echo "relay dll missing at $RELAY"; exit 98; }
dotnet "$RELAY" >"$LOG/relay.log" 2>&1 & pids+=($!)
for i in $(seq 1 30); do curl -fsS "http://localhost:$PORT/health" >/dev/null 2>&1 && { echo "relay ready"; break; }; sleep 0.5; done

dotnet "$AGENT" send --relay "$WS" --room demo $AFLAG --source synthetic $VFLAG >"$LOG/send.log" 2>&1 & pids+=($!)
sleep 1
dotnet "$AGENT" receive --relay "$WS" --room demo $AFLAG --publish-pipewire "$NODE" $VFLAG >"$LOG/recv.log" 2>&1 & RECV=$!; pids+=($RECV)

sleep 8

echo "=== gst dmabuf flow + content check (GL import via compositor, last frame read back) ==="
export GST_GL_WINDOW=wayland
rm -f "$LOG/seq.rgba"
# Read the published dmabuf frames back to CPU (GL import -> download) and inspect the LAST frame's pixels.
# This validates the GPU zero-copy output content, not just that frames flowed (the alpha gradient / live colour).
gst-launch-1.0 -q pipewiresrc target-object="$NODE" num-buffers=30 ! glupload ! glcolorconvert ! gldownload \
  ! video/x-raw,format=RGBA ! filesink location="$LOG/seq.rgba" >"$LOG/gst-flow.log" 2>&1
FLOW=$?
echo "gst flow exit=$FLOW"; tail -2 "$LOG/gst-flow.log"
CONTENT=0
if [ -s "$LOG/seq.rgba" ]; then
  CONTENT=$(python3 - "$LOG/seq.rgba" "$ALPHA" <<'PY'
import sys
d=open(sys.argv[1],"rb").read(); alpha=sys.argv[2]=="1"
fr=1280*720*4; n=len(d)//fr
if n==0: print(0); sys.exit()
last=d[(n-1)*fr:n*fr]
R=last[0::4]; G=last[1::4]; B=last[2::4]; A=last[3::4]
live = len(set(R))>8 and len(set(G))>8 and (max(R)-min(R))>32     # colour varies => live picture
ok = live and (len(set(A))>8 if alpha else min(A)==255)           # alpha: gradient; opaque: fully opaque
sys.stderr.write(f"frames={n} Rrange={min(R)}-{max(R)} Adistinct={len(set(A))} Amin/max={min(A)}/{max(A)}\n")
print(1 if ok else 0)
PY
)
  echo "content-ok=$CONTENT"
fi

# Did the receiver survive (the GC-dangling-hook crash regressed here before)?
if kill -0 "$RECV" 2>/dev/null; then RECV_ALIVE=1; else RECV_ALIVE=0; fi
echo "receiver alive=$RECV_ALIVE"
echo "=== receiver tail ==="; grep -iE "pw-out|pw-sink|OnFormat|alloc|served|state|segmentation|exception" "$LOG/recv.log" | tail -15

if [ "$FLOW" -eq 0 ] && [ "$RECV_ALIVE" -eq 1 ] && [ "$CONTENT" = "1" ]; then
  echo "PIPEWIRE-PUBLISH-LOOPBACK-PASS (alpha=$ALPHA, content verified)"
else
  echo "PIPEWIRE-PUBLISH-LOOPBACK-FAIL flow=$FLOW recvAlive=$RECV_ALIVE content=$CONTENT (alpha=$ALPHA)"
fi
