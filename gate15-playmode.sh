#!/usr/bin/env bash
# GATE 1.5 — Play Mode Torture Test (see unity-bridge-v1.5-task-brief.md,
# "GATE 1.5" section).
#
# Run it (one sentence): `./gate15-playmode.sh` from a shell with Unity
# already open and the bridge listening in the sandbox project — fully
# unattended, no alt-tab needed (unlike gate1-torture.sh, nothing here
# requires a human-triggered recompile).
#
# Repeat 5 times: one deliberate wrong-token /act/playmode/enter (must 401,
# must not change state) -> real enter (with token) -> reconnect contract
# -> confirm readyState:playmode -> real exit (with token) -> reconnect
# contract -> confirm readyState:ready.
#
# Fails loudly and stops on the first failed pass condition, per the same
# rule as GATE 1: no partial passes, no rationalizing a near-pass — fix and
# rerun this script from iteration 1.

set -uo pipefail

SANDBOX="/c/Users/DalyF/Documents/GitHub/Unity MCP"
PORT_FILE="$SANDBOX/Library/UnityBridge/port"
TOKEN_FILE="$SANDBOX/Library/UnityBridge/token"

ITERATIONS=5
POLL_INTERVAL=0.15
CEILING_MS=30000
SETTLE_SECONDS=5
WRONG_TOKEN="0000000000000000000000000000000000000000000000000000000000ff"

if [ ! -f "$PORT_FILE" ]; then
  echo "ERROR: $PORT_FILE not found — is Unity open with the bridge listening?" >&2
  exit 1
fi
if [ ! -f "$TOKEN_FILE" ]; then
  echo "ERROR: $TOKEN_FILE not found — has the bridge started at least once since the v1.5 token code landed?" >&2
  exit 1
fi

PORT=$(tr -d '[:space:]' < "$PORT_FILE")
TOKEN=$(tr -d '[:space:]' < "$TOKEN_FILE")

WRONG_BODY_FILE=$(mktemp)
ENTER_BODY_FILE=$(mktemp)
EXIT_BODY_FILE=$(mktemp)
trap 'rm -f "$WRONG_BODY_FILE" "$ENTER_BODY_FILE" "$EXIT_BODY_FILE"' EXIT

echo "GATE 1.5 — Play Mode Torture Test"
echo "Port: $PORT"
echo ""

read_state() {
  curl -s -m 2 "http://127.0.0.1:${PORT}/ping" 2>/dev/null | grep -oE '"readyState":"[a-zA-Z]+"' | sed -E 's/.*:"([a-zA-Z]+)"/\1/'
}

# Reconnect-contract-style poll: keep trying (refused/reset/non-target
# state all just mean "keep polling") until readyState matches $1 or the
# 30s ceiling is hit. Echoes elapsed ms on success.
wait_for_state() {
  local target="$1"
  local start_ms=$(date +%s%3N)
  local deadline_ms=$((start_ms + CEILING_MS))
  while [ "$(date +%s%3N)" -lt "$deadline_ms" ]; do
    state=$(read_state)
    if [ "$state" = "$target" ]; then
      echo $(( $(date +%s%3N) - start_ms ))
      return 0
    fi
    sleep "$POLL_INTERVAL"
  done
  return 1
}

RESULTS=()

for i in $(seq 1 "$ITERATIONS"); do
  echo "=== Iteration $i/$ITERATIONS ==="

  # --- negative test: wrong token must 401 and must not touch state ---
  wrong_code=$(curl -s -o "$WRONG_BODY_FILE" -w "%{http_code}" -X POST -d '' \
    -H "X-Bridge-Token: $WRONG_TOKEN" "http://127.0.0.1:${PORT}/act/playmode/enter")
  if [ "$wrong_code" != "401" ]; then
    echo "FAIL — wrong-token request returned HTTP $wrong_code (expected 401). Body: $(cat "$WRONG_BODY_FILE")"
    exit 1
  fi
  state_after_wrong=$(read_state)
  if [ "$state_after_wrong" != "ready" ]; then
    echo "FAIL — wrong-token request appears to have changed play state (readyState=$state_after_wrong, expected still ready)."
    exit 1
  fi
  echo "wrong-token negative test: 401 confirmed, state unchanged (ready)"

  # --- real enter ---
  enter_code=$(curl -s -o "$ENTER_BODY_FILE" -w "%{http_code}" -X POST -d '' \
    -H "X-Bridge-Token: $TOKEN" "http://127.0.0.1:${PORT}/act/playmode/enter")
  if [ "$enter_code" != "202" ]; then
    echo "FAIL — /act/playmode/enter returned HTTP $enter_code (expected 202). Body: $(cat "$ENTER_BODY_FILE")"
    exit 1
  fi

  enter_ms=$(wait_for_state "playmode") || { echo "FAIL — never observed readyState:playmode within ${CEILING_MS}ms after enter."; exit 1; }
  enter_display=$(awk -v ms="$enter_ms" 'BEGIN { printf "%.1f", ms/1000 }')
  echo "enter confirmed playmode in ${enter_display}s"

  # --- real exit ---
  exit_code=$(curl -s -o "$EXIT_BODY_FILE" -w "%{http_code}" -X POST -d '' \
    -H "X-Bridge-Token: $TOKEN" "http://127.0.0.1:${PORT}/act/playmode/exit")
  if [ "$exit_code" != "202" ]; then
    echo "FAIL — /act/playmode/exit returned HTTP $exit_code (expected 202). Body: $(cat "$EXIT_BODY_FILE")"
    exit 1
  fi

  exit_ms=$(wait_for_state "ready") || { echo "FAIL — never observed readyState:ready within ${CEILING_MS}ms after exit."; exit 1; }
  exit_display=$(awk -v ms="$exit_ms" 'BEGIN { printf "%.1f", ms/1000 }')
  echo "exit confirmed ready in ${exit_display}s"

  file_port=$(tr -d '[:space:]' < "$PORT_FILE")
  if [ "$file_port" != "$PORT" ]; then
    echo "FAIL — port file ($file_port) does not match the port this run started on ($PORT). Discovery-file pass condition violated."
    exit 1
  fi

  echo "PASS — iteration $i: enter ${enter_display}s, exit ${exit_display}s, port stable ($PORT), wrong-token 401 confirmed"
  RESULTS+=("iteration $i: enter ${enter_display}s, exit ${exit_display}s, wrong-token 401 confirmed, port=$PORT")

  # Diagnostic-only settle pause (2026-07-18, added after an Editor crash
  # immediately following a clean 5/5 run — working hypothesis: rapid,
  # repeated play-mode/domain-reload churn stressed the graphics pipeline).
  # Test-script pacing only; does NOT change any shipped endpoint
  # behavior. If this holds up, the real fix belongs in the endpoints
  # themselves (a shipped cooldown), not just in how this gate paces
  # itself — a test-only workaround wouldn't protect a real caller.
  if [ "$i" -lt "$ITERATIONS" ]; then
    echo "(settling ${SETTLE_SECONDS}s before next iteration...)"
    sleep "$SETTLE_SECONDS"
  fi
  echo ""
done

echo "=== GATE 1.5: PASSED (5/5 iterations, 10/10 transitions confirmed, 5/5 wrong-token checks) ==="
for r in "${RESULTS[@]}"; do
  echo "  $r"
done
echo ""
echo "Next: log these results in the package README under a 'GATE 1.5 RESULTS' heading with date and Unity version."
