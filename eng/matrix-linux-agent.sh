#!/usr/bin/env bash
# Linux-side agent launcher for the cross-machine verify matrix (eng/verify-matrix.ps1 calls this over SSH).
# Sets the runtime env the handheld needs - dotnet location, and the Wayland session so the GPU PipeWire
# publish/verify path (VAAPI decode -> VPP -> dmabuf -> readback) has a compositor - then execs the agent
# with whatever send/receive args the orchestrator passes. Kept tiny so the SSH command line stays quote-free.
set -u
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/1000}"
export WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}"
AGENT="$HOME/stx/samples/StreamTransport.Agent/bin/Release/net11.0/streamtransport-agent.dll"
[ -f "$AGENT" ] || { echo "MATRIX-LINUX-AGENT-MISSING: $AGENT"; exit 97; }
exec dotnet "$AGENT" "$@"
