#!/usr/bin/env bash
# GATE 1 — Reload Torture Test (see unity-bridge-task-brief.md, "GATE 1" section).
#
# Run it (one sentence): `./gate1-torture.sh` from a shell with Unity already
# open and the bridge listening; each iteration edits a file and starts
# polling /ping immediately, so as soon as it prints the alt-tab prompt,
# switch to Unity right away — the reload is fast (~2-3s observed) and the
# poll clock is already running, not waiting on you.
#
# Recompile of this file: package can only be triggered by Unity Editor
# window focus (no scripting/CLI hook exists to force it), so this script
# cannot be fully unattended — it needs a prompt alt-tab per iteration.
# Polling starts the instant the file is saved (matching the task brief's
# "immediately begin polling" instruction) rather than waiting for you to
# confirm you're back, so the fast reload window doesn't finish unobserved.
#
# Fails loudly and stops on the first failed pass condition, per the task
# brief's rule: no partial passes, no rationalizing 9/10 as a pass — fix
# Phase 1 and rerun this script from iteration 1.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BRIDGE_FILE="$SCRIPT_DIR/Editor/BridgeServer.cs"
SANDBOX="/c/Users/DalyF/Documents/GitHub/Unity MCP"
PORT_FILE="$SANDBOX/Library/UnityBridge/port"

ITERATIONS=10
POLL_INTERVAL=0.15
CEILING_MS=30000

if [ ! -f "$BRIDGE_FILE" ]; then
  echo "ERROR: $BRIDGE_FILE not found." >&2
  exit 1
fi

if ! grep -q "gate1-torture-marker" "$BRIDGE_FILE"; then
  echo "ERROR: $BRIDGE_FILE has no gate1-torture-marker line to toggle. Add one before running." >&2
  exit 1
fi

if [ ! -f "$PORT_FILE" ]; then
  echo "ERROR: $PORT_FILE not found — is Unity open with the bridge listening?" >&2
  exit 1
fi

initial_port=$(tr -d '[:space:]' < "$PORT_FILE")
echo "GATE 1 — Reload Torture Test"
echo "Initial bound port: $initial_port"
echo ""

RESULTS=()
all_states_seen=""

for i in $(seq 1 "$ITERATIONS"); do
  echo "=== Iteration $i/$ITERATIONS ==="

  sed -i -E "s#(gate1-torture-marker: )[0-9]+#\1$i#" "$BRIDGE_FILE"
  echo "Edited $BRIDGE_FILE (marker -> $i), saved. Polling starts NOW — alt-tab to Unity right away to trigger the recompile."

  start_ms=$(date +%s%3N)
  deadline_ms=$((start_ms + CEILING_MS))
  refused=0
  states_this_iter=""
  success=false
  final_port=""
  elapsed_ms=0
  # A bare "ready" response only counts as a genuine reconnect once we've
  # first witnessed the reload actually disrupt the old listener (a refusal,
  # or a non-ready state) — otherwise the very first poll would just hit the
  # still-running pre-edit domain and falsely "succeed" in 0s.
  saw_disruption=false

  while [ "$(date +%s%3N)" -lt "$deadline_ms" ]; do
    body=$(curl -s -m 2 "http://127.0.0.1:${initial_port}/ping" 2>/dev/null)
    if [ -n "$body" ]; then
      state=$(echo "$body" | grep -oE '"readyState":"[a-zA-Z]+"' | sed -E 's/.*:"([a-zA-Z]+)"/\1/')
      port_in_body=$(echo "$body" | grep -oE '"boundPort":[0-9]+' | grep -oE '[0-9]+')
      if [ -n "$state" ]; then
        case " $states_this_iter " in
          *" $state "*) ;;
          *) states_this_iter="$states_this_iter $state" ;;
        esac
        if [ "$state" != "ready" ] && [ "$state" != "playmode" ]; then
          saw_disruption=true
        elif [ "$saw_disruption" = true ]; then
          success=true
          final_port="$port_in_body"
          elapsed_ms=$(( $(date +%s%3N) - start_ms ))
          break
        fi
      fi
    else
      refused=$((refused + 1))
      saw_disruption=true
    fi
    sleep "$POLL_INTERVAL"
  done

  elapsed_display=$(awk -v ms="$elapsed_ms" 'BEGIN { printf "%.1f", ms/1000 }')

  if [ "$success" != true ]; then
    if [ "$saw_disruption" != true ]; then
      echo "FAIL — iteration $i never saw the listener disrupted at all within $((CEILING_MS/1000))s (no refusal, no non-ready state). This means the recompile likely never actually triggered — check that you alt-tabbed into Unity promptly."
    else
      echo "FAIL — iteration $i saw a disruption but never reconnected within $((CEILING_MS/1000))s. Refused attempts: $refused, states seen:$states_this_iter"
    fi
    echo ""
    echo "GATE 1 FAILED at iteration $i. Per the task brief: diagnose Phase 1, fix, and rerun this whole script from iteration 1 — no partial passes."
    exit 1
  fi

  if [ "$final_port" != "$initial_port" ]; then
    echo "FAIL — bound port changed mid-run ($initial_port -> $final_port). Pass condition violated (port must be identical across all 10 iterations)."
    exit 1
  fi

  file_port=$(tr -d '[:space:]' < "$PORT_FILE")
  if [ "$file_port" != "$final_port" ]; then
    echo "FAIL — port file ($file_port) does not match live port ($final_port) after reconnect. Discovery-file pass condition violated."
    exit 1
  fi

  echo "PASS — reconnected in ${elapsed_display}s, refused attempts: $refused, states seen:[$states_this_iter], port: $final_port"
  RESULTS+=("iteration $i: ${elapsed_display}s to reconnect, $refused refused attempts, states=[$states_this_iter], port=$final_port")
  all_states_seen="$all_states_seen$states_this_iter "
  echo ""

  if [ "$i" -lt "$ITERATIONS" ]; then
    read -r -p "Iteration $i done. Press Enter when ready to start iteration $((i+1)): " _
  fi
done

echo "=== Reconnect results: 10/10 iterations reconnected within the ceiling ==="
for r in "${RESULTS[@]}"; do
  echo "  $r"
done
echo ""

compiling_ok=false
if echo "$all_states_seen" | grep -qw "compiling"; then
  compiling_ok=true
  echo "compiling readyState observed at least once: YES"
else
  echo "compiling readyState observed at least once: NO"
fi

echo "Bound port stable across all iterations: $initial_port"
echo ""

if [ "$compiling_ok" = true ]; then
  echo "=== GATE 1: PASSED (10/10, all pass conditions met) ==="
  echo "Next: log these 10 entries in the package README under a 'GATE 1 RESULTS' heading with date and Unity version (G.3)."
else
  echo "=== GATE 1: NOT PASSED ==="
  echo "All 10 reconnects succeeded, but the 'compiling' readyState was never observed — that is a required pass condition (task brief G-conditions), not optional. Per the brief: do not rationalize this as a pass. Diagnose and rerun from iteration 1."
  exit 1
fi
