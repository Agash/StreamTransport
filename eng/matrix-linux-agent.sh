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
# Prefer the self-contained NativeAOT binary (the deployment artifact); fall back to the framework-dependent
# dll via the runtime only if the AOT publish is absent.
AOT="$HOME/stx/samples/StreamTransport.Agent/bin/Release/net11.0/linux-x64/publish/streamtransport-agent"
DLL="$HOME/stx/samples/StreamTransport.Agent/bin/Release/net11.0/streamtransport-agent.dll"
if [ -x "$AOT" ]; then
  exec "$AOT" "$@"
elif [ -f "$DLL" ]; then
  exec dotnet "$DLL" "$@"
else
  echo "MATRIX-LINUX-AGENT-MISSING: $AOT (build the linux-x64 AOT publish first)"; exit 97
fi
