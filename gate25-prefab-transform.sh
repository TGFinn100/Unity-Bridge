#!/usr/bin/env bash
# GATE 2.5 — Prefab/Transform Sequence Test (see
# unity-bridge-v2.5-task-brief.md, "Gate coverage" section).
#
# Run it (one sentence): `./gate25-prefab-transform.sh` from a shell with
# Unity already open and the bridge listening in the sandbox project —
# fully unattended, no alt-tab needed (none of these five endpoints trigger
# a domain reload, same reasoning as GATE 1.5/GATE 2).
#
# Repeat 5 times: instantiate -> transform/set (partial, then full) ->
# save-as-prefab -> apply -> revert. Checks HTTP status codes and
# response-shape/field-value correctness for all 5 endpoints.
#
# Deliberately does NOT script an Undo.PerformUndo() check — same gap
# gate2-mutation.sh already flagged and the user already resolved during
# v2 (see package README, "Deliberately not scripted here" / lines
# 157-166): no LOCKED endpoint exposes Undo.PerformUndo() over HTTP, and
# the v2.5 brief does not add one (confirmed during this brief's own
# pre-build gap-check). Undo-stack verification for apply's undoable:false
# and revert's Undo coverage was a separate human-driven Ctrl+Z check at
# sign-off, matching v2's own acceptance criterion 6 precedent exactly —
# both CONFIRMED (see package README DECISIONS, 2026-07-23): apply is
# genuinely non-undoable, revert genuinely is, so revert's response omits
# "undoable" entirely (step 6 below asserts it's absent, not true).
#
# Also regression-tests a real bug found and fixed during this slice's own
# build: PrefabUtility.SaveAsPrefabAssetAndConnect's return value is the
# saved PREFAB ASSET's own root GameObject (confirmed against Unity's
# scripting reference), not the reconnected scene instance — building the
# response from that return value instead of the original instanceRoot
# produced a response whose object.id/name pointed at the prefab asset
# instead of the live scene object. Step 4 below checks object.name
# against the INSTANCE's own name, not the prefab file's name, specifically
# to catch a regression of that bug.
#
# Fails loudly and stops on the first failed pass condition, per the same
# rule as every prior gate: no partial passes, no rationalizing a
# near-pass — fix and rerun this script from iteration 1.

set -uo pipefail

SANDBOX="/c/Users/DalyF/Documents/GitHub/Unity MCP"
PORT_FILE="$SANDBOX/Library/UnityBridge/port"
TOKEN_FILE="$SANDBOX/Library/UnityBridge/token"

# Existing fixture prefab (VariantBase, AudioSource) — resolved by name via
# /query at the top of the run rather than a hardcoded GUID, so this script
# doesn't silently break if the fixture is ever recreated with a new GUID.
FIXTURE_PREFAB_NAME="VariantBase"

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

echo "GATE 2.5 — Prefab/Transform Sequence Test"
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

call_noauth() {
  local method="$1" path="$2" json="${3:-}"
  curl -s -o "$BODY_FILE" -w "%{http_code}" -X "$method" \
    -H "Content-Type: application/json" -d "${json:-}" "${BASE}${path}"
}

extract_field() {
  # extract_field FILE JSONKEY -> first "key":"value" match's value.
  # Good enough for this script's flat, single-occurrence response shapes.
  grep -oE "\"$2\":\"[^\"]*\"" "$1" | head -1 | sed -E "s/\"$2\":\"([^\"]*)\"/\1/"
}

body_contains() {
  grep -q -- "$2" "$1"
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

# --- resolve the fixture prefab's GUID once, by name, via /query ---
code=$(curl -s -o "$BODY_FILE" -w "%{http_code}" -X POST "${BASE}/query" \
  -d "{\"type\":\"prefab\",\"nameGlob\":\"${FIXTURE_PREFAB_NAME}\"}")
expect_status "$code" 200 "resolve fixture prefab guid"
FIXTURE_GUID=$(extract_field "$BODY_FILE" guid)
[ -n "$FIXTURE_GUID" ] || fail "could not resolve '${FIXTURE_PREFAB_NAME}' prefab guid via /query — is the fixture still in the sandbox?"
echo "Fixture prefab '${FIXTURE_PREFAB_NAME}' guid: $FIXTURE_GUID"
echo ""

RESULTS=()
CLEANUP_IDS=()
CLEANUP_PATHS=()

for i in $(seq 1 "$ITERATIONS"); do
  echo "=== Iteration $i/$ITERATIONS ==="
  INSTANCE_NAME="Gate25Instance_${i}_$$"
  PREFAB_PATH="Assets/Prefabs/Gate25Saved_${i}_$$.prefab"

  # --- 1. instantiate ---
  code=$(call POST /act/prefab/instantiate "{\"prefabGuid\":\"$FIXTURE_GUID\",\"name\":\"$INSTANCE_NAME\"}")
  expect_status "$code" 200 "prefab/instantiate"
  body_contains "$BODY_FILE" '"m_LocalPosition":{"x":0,"y":0,"z":0}' || fail "instantiate: default position not zero"
  body_contains "$BODY_FILE" '"m_LocalRotation":{"x":0,"y":0,"z":0,"w":1}' || fail "instantiate: default rotation not identity"
  ID=$(extract_field "$BODY_FILE" id)
  [ -n "$ID" ] || fail "instantiate response had no object.id"
  echo "prefab/instantiate: OK (id captured, zero position, identity rotation)"

  # --- 2. transform/set, partial (position only) ---
  code=$(call POST /act/transform/set "{\"id\":\"$ID\",\"position\":{\"x\":10,\"y\":20,\"z\":30}}")
  expect_status "$code" 200 "transform/set (partial)"
  body_contains "$BODY_FILE" '"m_LocalPosition":{"x":10,"y":20,"z":30}' || fail "transform/set partial: position not updated"
  body_contains "$BODY_FILE" '"m_LocalRotation":{"x":0,"y":0,"z":0,"w":1}' || fail "transform/set partial: rotation changed but shouldn't have"
  echo "transform/set (partial): OK (position moved, rotation untouched)"

  # --- 2b. transform/set, no fields -> 400 no_fields ---
  code=$(call POST /act/transform/set "{\"id\":\"$ID\"}")
  expect_status "$code" 400 "transform/set (no fields)"
  body_contains "$BODY_FILE" '"error":"no_fields"' || fail "transform/set no-fields body missing error:no_fields"
  echo "transform/set (no fields): OK (400 no_fields confirmed)"

  # --- 3. transform/set, full (with non-identity quaternion) ---
  code=$(call POST /act/transform/set "{\"id\":\"$ID\",\"position\":{\"x\":1,\"y\":2,\"z\":3},\"rotation\":{\"x\":0,\"y\":0.7071068,\"z\":0,\"w\":0.7071068},\"scale\":{\"x\":2,\"y\":2,\"z\":2}}")
  expect_status "$code" 200 "transform/set (full)"
  body_contains "$BODY_FILE" '"m_LocalPosition":{"x":1,"y":2,"z":3}' || fail "transform/set full: position wrong"
  body_contains "$BODY_FILE" '"y":0.7071068' || fail "transform/set full: rotation.y wrong"
  body_contains "$BODY_FILE" '"m_LocalScale":{"x":2,"y":2,"z":2}' || fail "transform/set full: scale wrong"
  echo "transform/set (full): OK (position/rotation/scale all updated)"

  # --- 4. save-as-prefab ---
  code=$(call POST /act/prefab/save "{\"id\":\"$ID\",\"path\":\"$PREFAB_PATH\"}")
  expect_status "$code" 200 "prefab/save"
  # Regression check for the SaveAsPrefabAssetAndConnect return-value bug
  # found during this slice's build: object.name must be the INSTANCE's own
  # name, never the prefab asset's filename.
  body_contains "$BODY_FILE" "\"name\":\"$INSTANCE_NAME\"" || fail "prefab/save: object.name doesn't match the instance's own name — possible regression of the SaveAsPrefabAssetAndConnect return-value bug (response may be built from the asset's root GameObject instead of the reconnected scene instance)"
  PREFAB_GUID=$(extract_field "$BODY_FILE" guid)
  [ -n "$PREFAB_GUID" ] || fail "prefab/save response had no prefab.guid"
  # Real behavior found live while writing this gate script, not a bug:
  # /act/prefab/save changes the instance's GlobalObjectId (its
  # targetPrefabId component encodes which prefab source the instance is
  # CONNECTED to, and this call connects it to a brand-new prefab) — the
  # pre-save $ID is stale from this point on. Every subsequent call in this
  # iteration must use the id the save response itself returns, not the one
  # captured at instantiate time. Worth calling out explicitly in the skill
  # file's save-as-prefab idiom.
  SAVE_ID=$(extract_field "$BODY_FILE" id)
  [ -n "$SAVE_ID" ] || fail "prefab/save response had no object.id"
  CLEANUP_IDS+=("$SAVE_ID")
  CLEANUP_PATHS+=("$PREFAB_PATH")
  echo "prefab/save: OK (new prefab guid + post-save id captured, object.name correctly still the instance's own name)"

  # --- 4b. save-as-prefab retry at the same path -> 409 asset_exists ---
  code=$(call POST /act/prefab/save "{\"id\":\"$SAVE_ID\",\"path\":\"$PREFAB_PATH\"}")
  expect_status "$code" 409 "prefab/save (retry, same path)"
  body_contains "$BODY_FILE" '"error":"asset_exists"' || fail "prefab/save retry 409 body missing error:asset_exists"
  echo "prefab/save (retry): OK (409 asset_exists confirmed)"

  # --- 5. modify a field on the connected instance, then apply ---
  code=$(call POST /act/component/set-field "{\"id\":\"$SAVE_ID\",\"component\":\"AudioSource\",\"field\":\"m_Volume\",\"value\":0.3}")
  expect_status "$code" 200 "component/set-field (pre-apply)"

  code=$(call POST /act/prefab/apply "{\"id\":\"$SAVE_ID\"}")
  expect_status "$code" 200 "prefab/apply"
  body_contains "$BODY_FILE" '"undoable":false' || fail "prefab/apply: undoable:false missing"
  body_contains "$BODY_FILE" "\"prefabGuid\":\"$PREFAB_GUID\"" || fail "prefab/apply: applied.prefabGuid doesn't match the saved prefab"
  echo "prefab/apply: OK (undoable:false, applied.prefabGuid matches)"

  # --- criterion 9's real verification method: instantiate a FRESH second
  # copy of the same prefabGuid and confirm ITS field reflects the applied
  # value — proves the asset file itself changed, not just this instance's
  # own override still standing (the brief's originally-named /asset/{guid}
  # method can't show this — /asset/{guid} never serializes prefab
  # component field values, see the pre-build gap-check).
  code=$(call POST /act/prefab/instantiate "{\"prefabGuid\":\"$PREFAB_GUID\",\"name\":\"Gate25Verify_${i}_$$\"}")
  expect_status "$code" 200 "prefab/instantiate (fresh copy, verify apply)"
  VERIFY_ID=$(extract_field "$BODY_FILE" id)
  CLEANUP_IDS+=("$VERIFY_ID")
  body_contains "$BODY_FILE" '"m_Volume":0.3' || fail "apply did not actually update the prefab asset — fresh instance still shows the old m_Volume"
  echo "prefab/apply verification: OK (fresh instance of the saved prefab shows m_Volume:0.3)"

  # --- 6. modify again without applying, then revert ---
  code=$(call POST /act/component/set-field "{\"id\":\"$SAVE_ID\",\"component\":\"AudioSource\",\"field\":\"m_Volume\",\"value\":0.9}")
  expect_status "$code" 200 "component/set-field (pre-revert)"

  code=$(call POST /act/prefab/revert "{\"id\":\"$SAVE_ID\"}")
  expect_status "$code" 200 "prefab/revert"
  # "undoable" is omitted from revert's response (confirmed via a
  # human-driven Ctrl+Z check to be ordinary, always-true Undo coverage —
  # dropped per the omit-when-constant convention, same as
  # instantiate/transform-set/save) — must NOT reappear here.
  body_contains "$BODY_FILE" '"undoable"' && fail "prefab/revert: undoable field present but should be omitted (confirmed always-true, per the omit-when-constant convention)"
  body_contains "$BODY_FILE" '"m_Volume":0.3' || fail "prefab/revert: field did not snap back to the applied (0.3) value"
  echo "prefab/revert: OK (undoable omitted as expected, field snapped back to the applied value)"

  echo "PASS — iteration $i: all 5 endpoints exercised, save/apply/revert cycle confirmed"
  RESULTS+=("iteration $i: PASS")
  echo ""
done

echo "=== Cleanup: removing test GameObjects and prefab assets ==="
for id in "${CLEANUP_IDS[@]}"; do
  call POST /act/gameobject/delete "{\"id\":\"$id\",\"recursive\":false}" > /dev/null
done
for path in "${CLEANUP_PATHS[@]}"; do
  rm -f "$SANDBOX/$path" "$SANDBOX/$path.meta"
done
echo "Cleanup done (${#CLEANUP_IDS[@]} GameObjects, ${#CLEANUP_PATHS[@]} prefab assets)."
echo ""

echo "=== GATE 2.5: PASSED (${ITERATIONS}/${ITERATIONS} iterations) ==="
for r in "${RESULTS[@]}"; do
  echo "  $r"
done
echo ""
echo "Next: log these results in the package README under a 'GATE 2.5 RESULTS' heading with date and Unity version."
echo "Reminder: this script does not drive a real Ctrl+Z for apply/revert's Undo behavior —"
echo "that was a separate human-driven check at sign-off, already confirmed (see package README DECISIONS)."
