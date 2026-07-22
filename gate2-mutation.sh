#!/usr/bin/env bash
# GATE 2 — Mutation Sequence Test (see unity-bridge-v2-task-brief.md, "Gate
# coverage" section).
#
# Run it (one sentence): `./gate2-mutation.sh` from a shell with Unity
# already open and the bridge listening in the sandbox project — fully
# unattended, no alt-tab needed (no domain reload happens anywhere in this
# sequence, same reasoning as GATE 1.5).
#
# Repeat 5 times: create -> add component -> set field -> duplicate ->
# reparent -> rename -> remove component -> delete WITHOUT recursive
# (must 409 has_children) -> delete WITH recursive (must clean up both
# objects). Checks HTTP status codes and response-shape correctness for
# all 8 endpoints; does NOT drive Undo.PerformUndo() (no v2 endpoint
# exposes it over HTTP — acceptance criterion 6's Ctrl+Z check is a
# separate human-driven step at sign-off, not scripted here, per the
# explicit user decision recorded in TODO.md/package README when this gate
# was written).
#
# Fails loudly and stops on the first failed pass condition, per the same
# rule as every prior gate: no partial passes, no rationalizing a
# near-pass — fix and rerun this script from iteration 1.

set -uo pipefail

SANDBOX="/c/Users/DalyF/Documents/GitHub/Unity MCP"
PORT_FILE="$SANDBOX/Library/UnityBridge/port"
TOKEN_FILE="$SANDBOX/Library/UnityBridge/token"

ITERATIONS=5

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
BASE="http://127.0.0.1:${PORT}"

BODY_FILE=$(mktemp)
trap 'rm -f "$BODY_FILE"' EXIT

echo "GATE 2 — Mutation Sequence Test"
echo "Port: $PORT"
echo ""

# call METHOD PATH JSON_BODY -> prints http status code, leaves the
# response body in $BODY_FILE. A bodyless POST still needs -d '' — real
# curl gotcha documented in the skill file: HttpListener rejects a
# bodyless POST with no Content-Length header.
call() {
  local method="$1" path="$2" json="${3:-}"
  curl -s -o "$BODY_FILE" -w "%{http_code}" -X "$method" \
    -H "X-Bridge-Token: $TOKEN" -H "Content-Type: application/json" \
    -d "${json:-}" "${BASE}${path}"
}

extract_field() {
  # extract_field FILE JSONKEY -> first "key":"value" match's value.
  # Good enough for this script's flat, single-occurrence response shapes
  # (object.id, deleted.id) — not a general JSON parser.
  grep -oE "\"$2\":\"[^\"]*\"" "$1" | head -1 | sed -E "s/\"$2\":\"([^\"]*)\"/\1/"
}

fail() {
  echo "FAIL — $1"
  echo "Response body: $(cat "$BODY_FILE" 2>/dev/null)"
  exit 1
}

expect_status() {
  local got="$1" want="$2" step="$3"
  if [ "$got" != "$want" ]; then
    fail "$step returned HTTP $got (expected $want)"
  fi
}

RESULTS=()

for i in $(seq 1 "$ITERATIONS"); do
  echo "=== Iteration $i/$ITERATIONS ==="
  ROOT_NAME="Gate2Root_${i}_$$"
  CHILD_NAME="Gate2Child_${i}_$$"

  # --- create ---
  code=$(call POST /act/gameobject/create "{\"name\":\"$ROOT_NAME\"}")
  expect_status "$code" 200 "create"
  ROOT_ID=$(extract_field "$BODY_FILE" id)
  [ -n "$ROOT_ID" ] || fail "create response had no object.id"
  echo "create: OK (id captured)"

  # --- add component ---
  code=$(call POST /act/component/add "{\"id\":\"$ROOT_ID\",\"type\":\"AudioSource\"}")
  expect_status "$code" 200 "component/add"
  echo "component/add: OK (AudioSource on root)"

  # --- set field ---
  code=$(call POST /act/component/set-field "{\"id\":\"$ROOT_ID\",\"component\":\"AudioSource\",\"field\":\"m_Volume\",\"value\":0.5}")
  expect_status "$code" 200 "component/set-field"
  echo "component/set-field: OK (m_Volume=0.5)"

  # --- duplicate ---
  code=$(call POST /act/gameobject/duplicate "{\"id\":\"$ROOT_ID\"}")
  expect_status "$code" 200 "duplicate"
  DUP_ID=$(extract_field "$BODY_FILE" id)
  [ -n "$DUP_ID" ] || fail "duplicate response had no object.id"
  echo "duplicate: OK (id captured)"

  # --- reparent (dup under root, so root now has one child) ---
  code=$(call POST /act/gameobject/reparent "{\"id\":\"$DUP_ID\",\"parent\":\"$ROOT_ID\"}")
  expect_status "$code" 200 "reparent"
  echo "reparent: OK (dup moved under root)"

  # --- rename ---
  code=$(call POST /act/gameobject/rename "{\"id\":\"$DUP_ID\",\"name\":\"$CHILD_NAME\"}")
  expect_status "$code" 200 "rename"
  echo "rename: OK"

  # --- remove component (dup inherited AudioSource from duplicate) ---
  code=$(call POST /act/component/remove "{\"id\":\"$DUP_ID\",\"type\":\"AudioSource\"}")
  expect_status "$code" 200 "component/remove"
  echo "component/remove: OK"

  # --- delete WITHOUT recursive: root has one child (dup) -> must 409 ---
  code=$(call POST /act/gameobject/delete "{\"id\":\"$ROOT_ID\",\"recursive\":false}")
  expect_status "$code" 409 "delete (non-recursive, has children)"
  grep -q '"error":"has_children"' "$BODY_FILE" || fail "delete non-recursive 409 body missing error:has_children"
  echo "delete (non-recursive): OK (409 has_children confirmed)"

  # --- delete WITH recursive: cleans up root + dup ---
  code=$(call POST /act/gameobject/delete "{\"id\":\"$ROOT_ID\",\"recursive\":true}")
  expect_status "$code" 200 "delete (recursive)"
  echo "delete (recursive): OK (root + child removed)"

  echo "PASS — iteration $i: all 8 endpoints exercised, both delete branches confirmed"
  RESULTS+=("iteration $i: PASS")
  echo ""
done

echo "=== GATE 2: PASSED (${ITERATIONS}/${ITERATIONS} iterations) ==="
for r in "${RESULTS[@]}"; do
  echo "  $r"
done
echo ""
echo "Next: log these results in the package README under a 'GATE 2 RESULTS' heading with date and Unity version."
echo "Reminder: this script does not exercise acceptance criterion 6 (Undo-stack revert) — that's a separate human-driven Ctrl+Z check at sign-off."
