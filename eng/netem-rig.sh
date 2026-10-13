#!/usr/bin/env bash
# Runs a publisher and a measuring subscriber across a shaped link: two network namespaces joined by a veth
# pair, the link's rate, delay, jitter and loss set with tc, the signalling relay on the far side. Each
# profile publishes the test signal for a while and reports how the sender's target rate followed the
# link and what the receiver measured (latency, frames, A/V offset).
#
#   eng/netem-rig.sh [--seconds N] [profile ...]
#
# Profiles: clean lte jitter loss lossy step bloat l4s (all of them by default). jitter and loss are
# lte's two halves, to tell which one a result comes from. Linux only; needs root through
# sudo -n for the namespaces and tc, and runs the processes themselves as the calling user. IPv6 only, as
# the deployments are. Results go to artifacts/netem/<profile>/.
set -euo pipefail

seconds=60
profiles=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --seconds) seconds="$2"; shift 2 ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) profiles+=("$1"); shift ;;
  esac
done
[[ ${#profiles[@]} -gt 0 ]] || profiles=(clean lte jitter loss lossy step bloat l4s)

repo="$(cd "$(dirname "$0")/.." && pwd)"
out="$repo/artifacts/netem"
bin="$out/bin"
ns_a=st-rig-a
ns_b=st-rig-b
addr_a=fd5e:57::1
addr_b=fd5e:57::2
uid="$(id -u)"
gid="$(id -g)"
dotnet="$(command -v dotnet)"

sudo -n true 2>/dev/null || { echo "needs passwordless sudo for ip netns and tc" >&2; exit 2; }

echo "building the CLI and the relay"
"$dotnet" publish "$repo/samples/StreamTransport.Cli" -c Release -o "$bin/cli" -v q -nologo >/dev/null
"$dotnet" publish "$repo/samples/StreamTransport.Relay" -c Release -o "$bin/relay" -v q -nologo >/dev/null

teardown() {
  sudo -n ip netns pids "$ns_a" 2>/dev/null | xargs -r sudo -n kill 2>/dev/null || true
  sudo -n ip netns pids "$ns_b" 2>/dev/null | xargs -r sudo -n kill 2>/dev/null || true
  sudo -n ip netns del "$ns_a" 2>/dev/null || true
  sudo -n ip netns del "$ns_b" 2>/dev/null || true
}
trap teardown EXIT

link_up() {
  teardown
  sudo -n ip netns add "$ns_a"
  sudo -n ip netns add "$ns_b"
  sudo -n ip link add rig-a netns "$ns_a" type veth peer name rig-b netns "$ns_b"
  for side in a b; do
    local ns="st-rig-$side" addr="addr_$side"
    sudo -n ip -n "$ns" link set lo up
    sudo -n ip -n "$ns" link set "rig-$side" up
    sudo -n ip -n "$ns" addr add "${!addr}/64" dev "rig-$side" nodad
  done
}

# tc in a namespace.
qdisc() { local ns="$1"; shift; sudo -n ip netns exec "$ns" tc qdisc "$@"; }

# Delay each way, so the round trip is twice the argument; the rate and loss limit the publisher's side,
# which is the direction media takes.
shape() {
  local rate="$1" delay="$2" jitter="$3" loss="$4" limit="${5:-1000}"
  qdisc "$ns_a" replace dev rig-a root netem rate "$rate" delay "$delay" "$jitter" loss "$loss" limit "$limit"
  qdisc "$ns_b" replace dev rig-b root netem delay "$delay" "$jitter"
}

# A DualPI2 bottleneck (RFC 9332): the rate is an HTB class the AQM sits under, the delay a netem stage on
# the way back, so ECN marks come from the queue and not from netem.
shape_l4s() {
  local rate="$1" delay="$2"
  qdisc "$ns_a" replace dev rig-a root handle 1: htb default 10
  sudo -n ip netns exec "$ns_a" tc class add dev rig-a parent 1: classid 1:10 htb rate "$rate"
  qdisc "$ns_a" add dev rig-a parent 1:10 dualpi2
  qdisc "$ns_b" replace dev rig-b root netem delay "$(( ${delay%ms} * 2 ))ms"
}

# Runs a command in a namespace as the calling user.
as_user() {
  local ns="$1"; shift
  sudo -n ip netns exec "$ns" setpriv --reuid="$uid" --regid="$gid" --init-groups \
    env HOME="$HOME" PATH="$PATH" DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$dotnet")")}" "$@"
}

run_profile() {
  local profile="$1" dir="$out/$1"
  rm -rf "$dir" && mkdir -p "$dir"
  link_up
  case "$profile" in
    clean) shape 50mbit 10ms 0ms 0% ;;
    lte) shape 8mbit 25ms 8ms 0.5% ;;
    jitter) shape 8mbit 25ms 8ms 0% ;;
    loss) shape 8mbit 25ms 0ms 0.5% ;;
    lossy) shape 20mbit 20ms 2ms 3% ;;
    step) shape 20mbit 20ms 0ms 0% ;;
    bloat) shape 4mbit 20ms 0ms 0% 2000 ;;
    l4s) shape_l4s 10mbit 15ms ;;
    *) echo "unknown profile $profile" >&2; return 1 ;;
  esac

  as_user "$ns_b" env STREAMTRANSPORT_RELAY_URLS="http://[$addr_b]:8080" \
    "$dotnet" "$bin/relay/StreamTransport.Relay.dll" >"$dir/relay.log" 2>&1 &
  sleep 3
  local relay="ws://[$addr_b]:8080/ws"
  # The publisher's hello creates the room, so it goes first.
  as_user "$ns_a" "$dotnet" "$bin/cli/streamtransport.dll" publish --relay "$relay" --room rig \
    --video test --audio test >"$dir/publisher.log" 2>&1 &
  sleep 3
  as_user "$ns_b" "$dotnet" "$bin/cli/streamtransport.dll" subscribe --relay "$relay" --room rig \
    --video none --audio none --measure >"$dir/subscriber.log" 2>&1 &

  # The step profile drops the link to an eighth a third of the way in and restores it at two thirds.
  if [[ "$profile" == step ]]; then
    sleep $(( seconds / 3 )); shape 2500kbit 20ms 0ms 0%
    sleep $(( seconds / 3 )); shape 20mbit 20ms 0ms 0%
    sleep $(( seconds - 2 * (seconds / 3) ))
  else
    sleep "$seconds"
  fi

  sudo -n ip netns exec "$ns_a" tc -s qdisc show dev rig-a >"$dir/qdisc.txt" 2>&1 || true
  teardown
  sleep 1

  echo "== $profile"
  grep -oE "kbit/s target|[0-9]+ kbit/s target" "$dir/publisher.log" | grep -oE "^[0-9]+" | tr '\n' ' ' \
    | sed 's/^/  target kbit\/s every 5 s: /'; echo
  grep -E "Test signal:" "$dir/subscriber.log" | tail -1 | sed 's/^.*Test signal: /  received: /'
  grep -oE "decoded [0-9]+ video" "$dir/subscriber.log" | tail -1 | sed 's/^/  /'
}

for p in "${profiles[@]}"; do run_profile "$p"; done
