#!/bin/sh
# Starts coturn (STUN + TURN) and the StreamTransport signaling relay in one container. coturn provides
# STUN+TURN on 3478; the relay advertises it (with ephemeral credentials) and serves WebSocket signaling
# on 8080. A single shared secret ties them together.
set -eu

: "${STREAMTRANSPORT_ADVERTISED_HOST:=localhost}"
: "${TURN_REALM:=streamtransport}"
: "${TURN_MIN_PORT:=49160}"
: "${TURN_MAX_PORT:=49200}"

# A per-container shared secret unless one is supplied. Restarting rotates it, which is fine: clients
# fetch fresh ICE credentials when they (re)join a room.
if [ -z "${STREAMTRANSPORT_TURN_SECRET:-}" ]; then
  STREAMTRANSPORT_TURN_SECRET="$(LC_ALL=C tr -dc 'a-f0-9' < /dev/urandom | head -c 64)"
fi
export STREAMTRANSPORT_TURN_SECRET

# --- coturn config (TURN REST API ephemeral credentials via static-auth-secret) ---
CONF=/tmp/turnserver.conf
{
  echo "listening-port=3478"
  echo "fingerprint"
  echo "use-auth-secret"
  echo "static-auth-secret=${STREAMTRANSPORT_TURN_SECRET}"
  echo "realm=${TURN_REALM}"
  echo "no-cli"
  echo "no-multicast-peers"
  echo "min-port=${TURN_MIN_PORT}"
  echo "max-port=${TURN_MAX_PORT}"
} > "$CONF"
# external-ip lets coturn advertise the right public address behind NAT (set TURN_EXTERNAL_IP to the
# container host's public IP in production).
[ -n "${TURN_EXTERNAL_IP:-}" ] && echo "external-ip=${TURN_EXTERNAL_IP}" >> "$CONF"

echo "[entrypoint] starting coturn on 3478 (realm ${TURN_REALM})"
turnserver -c "$CONF" &
TURN_PID=$!

# Stop coturn if it dies so the container exits and the orchestrator restarts it.
trap 'kill "$TURN_PID" 2>/dev/null || true' INT TERM

# --- relay: advertise coturn, do not run our own STUN ---
export STREAMTRANSPORT_STUN_DISABLED=1
export STREAMTRANSPORT_RELAY_URLS="${STREAMTRANSPORT_RELAY_URLS:-http://0.0.0.0:8080}"
export STREAMTRANSPORT_TURN_URLS="turn:${STREAMTRANSPORT_ADVERTISED_HOST}:3478?transport=udp,turn:${STREAMTRANSPORT_ADVERTISED_HOST}:3478?transport=tcp"

echo "[entrypoint] starting StreamTransport relay on ${STREAMTRANSPORT_RELAY_URLS}"
exec dotnet StreamTransport.Relay.dll
