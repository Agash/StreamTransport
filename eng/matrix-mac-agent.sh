#!/usr/bin/env bash
# macOS-side agent launcher for the cross-machine verify matrix (eng/verify-matrix.ps1 calls this over SSH).
# Puts Homebrew on PATH so the agent's FFmpeg (VideoToolbox-enabled, 8.x) resolves, locates the published
# app-bundle binary, then execs it with whatever send/receive args the orchestrator passes. Kept tiny so the
# SSH command line stays quote-free.
set -u
export PATH="/opt/homebrew/bin:/usr/local/bin:$PATH"
AGENT="$(find "$HOME/repos/StreamTransport/samples/StreamTransport.Agent/bin/Release" \
  -path '*osx-arm64*' -name streamtransport-agent -type f 2>/dev/null | head -1)"
[ -n "$AGENT" ] && [ -f "$AGENT" ] || { echo "MATRIX-MAC-AGENT-MISSING (build the net11.0-macos agent first)"; exit 97; }
exec "$AGENT" "$@"
