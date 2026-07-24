# Response envelope conventions

All bodies are `Dictionary<string, object>`, serialized via `MiniJson.Write`
(`Editor/Json/MiniJson.cs`) — compact, no pretty-printing (LOCKED, the
consumer is a model).

## Fields every successful response carries

- `"tier"`: `"meta" | "indexed" | "live" | "act"` — always present.
- `"indexedAt"`: ISO timestamp (`IndexStore.LastUpdatedIso`) — every
  indexed-tier response, plus `/ping` (meta tier), which also reports it
  alongside `schemaVersion` so a caller can check index freshness without
  an indexed-tier round trip.
- `"frame"`: `Time.frameCount`, added by `BridgeState.AddFrameIfPlaying(body)`
  to any live-tier response when `EditorApplication.isPlaying` — the
  brief's "live answers are frame-stale by definition in play mode"
  disclosure. Every live handler calls this after building its body.
- **v2/v2.5, all 13 synchronous mutation endpoints:** `"autoSaved"` (bool) —
  every successful (200) mutation response, reflecting
  `MutationAutoSave.Enabled` at call time (see `shared-helpers.md`). No
  `"accepted"`/`"willReload"` on these — the change already happened by
  response time (200, not 202); see `routing-and-tiers.md`'s "Synchronous
  mutation dispatch (v2; extended v2.5)" section.
- **v2.5, `/act/prefab/apply` only:** `"undoable": false`, always present.
  Every other v2.5 mutation (`instantiate`, `transform/set`, `save`,
  `revert`) omits this field entirely, matching `willReload`'s own
  omit-when-constant convention — they're ordinary Undo-tracked mutations
  where the field would just be always-true-and-uninformative. `apply` is
  the one exception (confirmed live, not assumed — see
  `routing-and-tiers.md`'s Undo-integration note in the "Synchronous
  mutation dispatch" section), so it's the one endpoint that actually needs
  to say so.
- `"accepted"` / `"willReload"`: act-tier only, on a 202. `willReload` is
  best-effort (`/act/refresh` always reports `true`; `/act/playmode/*`
  derives it from Configurable Enter Play Mode settings via
  `BridgeState.CachedWillReloadOnPlaymodeToggle`; v1.6's
  `/act/logs/watch`/`/act/logs/unwatch` always report `false` — neither ever
  triggers a domain reload) — see `routing-and-tiers.md`'s `act` tier
  section for the accepted-then-reconnect contract this supports.
- **v1.7, `/ping` only:** `"compileErrorCount"` / `"compileWarningCount"`
  (int, true full counts) / `"compileMessages"` (capped list, errors before
  warnings — see `shared-helpers.md`'s `BridgeState` entry) /
  `"compileMessagesTruncated"` (bool) / `"compileMessagesTotal"` (int, true
  count before capping) / `"compileMessagesHint"` (string, present only when
  truncated: `"read Editor.log directly for full output"`). Deliberately
  separate field names from the truncated/total/hint trio below — `/ping`
  isn't a list-response endpoint in that trio's existing sense, and reusing
  the bare names would create ambiguity if `/ping` ever grows a second
  cappable field later (LOCKED, v1.7 task brief).

## The truncated/total/hint trio (LOCKED)

`ResponseCapping.ApplyListCap(body, listKey, total, hint)`
(`Editor/ResponseCapping.cs`) is the shared implementation: while the whole
body's JSON exceeds 16384 bytes, `RemoveAt(list.Count - 1)` on
`body[listKey]` (structural truncation — whole items dropped, never
byte-level, so the response always parses). Then sets `body["truncated"]`,
`body["total"]` (the true match count *before* capping), and
`body["hint"]` (only when truncated) naming the narrowing move.

**This helper only handles one flat top-level list.** Two precedents for
when a response's shape doesn't fit that:

- `AssetEndpoint`'s scene branch has two candidate lists (`components` and
  `prefabInstances`). `ApplyListCap` shrinks `components`; a manual
  best-effort loop (`while (... > 16384) instanceList.RemoveAt(...)`)
  handles `prefabInstances` without its own truncation metadata (the
  LOCKED trio is meaningless split across two lists in one response).
- `ObjectEndpoint`'s object tree is nested (`children` inside `children`,
  up to depth 2), not flat. It writes its own recursive trim
  (`FindWidestChildrenList` + `TrimToCap`): repeatedly drop the last child
  of whichever node — anywhere in the tree — currently has the most direct
  children, until the whole body fits, then set `truncated`/`total`/`hint`
  manually at the top level. Same structural-truncation contract, just
  walked recursively instead of over one list.

If you're adding an endpoint with a genuinely new response shape (not one
flat list, not a small fixed tree), check whether either precedent above
fits before inventing a third pattern.

## Error shapes, by cause

- Handler throws `BridgeHttpException(code, message)` → `{"error": message}`
  at that status code. No `tier` key added automatically — callers that
  want `tier` in an error body add it themselves (see 503/504 below).
- Indexed-tier request while `!IndexStore.IsReady` → 503
  `{"tier":"indexed","error":"indexing","readyState":...}`.
- Live/queued-tier request that hits `endpoint.TimeoutMs` → 504
  `{"tier":endpoint.Tier,"error":"timeout","readyState":...,"elapsedMs":...}`.
- Uncaught exception in a handler → 500 `{"error": ex.Message}`.
- No route matches → 404 `{"error":"unknown endpoint — see /help"}`.
- Non-localhost `Host` header → 403 `{"error":"Host header must be localhost or 127.0.0.1"}`.
- Malformed POST JSON body → 400 `{"error":"malformed JSON body"}`.
- **`act`-tier only** (v1.5, checked in this order — see `routing-and-tiers.md`):
  - Missing/invalid `X-Bridge-Token` header → 401
    `{"error":"unauthorized","tier":"act"}`, unconditionally first.
  - `readyState` not `"ready"`/`"playmode"` → 503
    `{"tier":"act","error":"not_ready","readyState":...}` (never queue an
    action across a reload).
  - Another action already scheduled but not yet applied → 409
    `{"tier":"act","error":"action_pending"}`.
  - Action would be a no-op given current state (e.g. entering play mode
    while already in it) → 409
    `{"tier":"act","error":"already_in_state","current":...}`.
  - `/act/playmode/*` only: a toggle within 1000ms of the last accepted one
    → 429 `{"tier":"act","error":"cooldown","retryAfterMs":...}`.
  - **v1.7, `/act/playmode/enter` only:** a compile error is currently
    cached (`BridgeState.CachedCompileErrorCount > 0`) → 409
    `{"tier":"act","error":"compile_errors_present","compileErrorCount":...,"compileMessages":[...]}`,
    checked after `already_in_state`, before scheduling — same root bug the
    `/ping` fields above fix, not a separate one. `/act/playmode/exit` and
    `/act/refresh` are unaffected by this check.
  - **v1.6, `/act/logs/watch`/`/act/logs/unwatch` only:**
    - `/act/logs/watch` with a missing/blank `name` or `pattern`, or a
      non-positive `capacity` → 400
      `{"tier":"act","error":"invalid_request","detail":...}`.
      `/act/logs/unwatch` with a missing/blank `name` → the same shape
      (unwatch has no `pattern`/`capacity` params to validate).
    - `/act/logs/watch` with a `name` already registered → 409
      `{"tier":"act","error":"already_watching","name":...}`.
    - `/act/logs/watch` with a pattern that fails `Regex` compilation → 400
      `{"tier":"act","error":"invalid_pattern","detail":...}` (the .NET
      regex engine's own exception message).
    - `/act/logs/unwatch` with an unregistered `name` → 404
      `{"tier":"act","error":"not_watching","name":...}`.
  - **v2, the 8 GameObject-lifecycle/component-mutation endpoints only**
    (`Synchronous = true` — see `routing-and-tiers.md`): a `MutationRejection`
    thrown from `BuildMutation` carries its own full status+body (checked
    ahead of `BridgeHttpException` in `DrainMutationQueue`'s catch chain), so
    these shapes are hand-built per endpoint rather than derived from a
    fixed `{"error": message}`:
    - `id`-resolution failures (400 invalid id format / 404 stale id) reuse
      `GameObjectResolver.ResolveOrThrow`'s `BridgeHttpException`s
      **verbatim** (LOCKED) — same messages as `GET /object/{id}`, no
      `"tier"` key (consistent with the generic `BridgeHttpException` rule
      above).
    - `/act/gameobject/create`, `/act/gameobject/rename`: missing/empty
      `name` → 400 `{"tier":"act","error":"missing_name"}`.
    - `/act/gameobject/delete`: has children and `recursive` isn't `true` →
      409 `{"tier":"act","error":"has_children","childCount":N,"hint":...}`
      — nothing deleted.
    - `/act/component/add`, `/act/component/remove`,
      `/act/component/set-field` (type resolution, shared via
      `ComponentTypeResolver`): no type with that name → 400
      `{"tier":"act","error":"unknown_component_type","type":...}`; more
      than one type shares that short name → 400
      `{"tier":"act","error":"ambiguous_type","type":...,"candidates":[...
      full type names...]}` — retry with a fully-qualified name (matched
      exactly against `Type.FullName`, checked before short-name matching so
      the retry can't hit the same ambiguity again).
    - `/act/component/remove`, `/act/component/set-field`: GameObject
      doesn't have that component → 404
      `{"tier":"act","error":"component_not_found","type":...}`.
    - `/act/component/remove`: another component's `[RequireComponent]`
      references the target type → 409
      `{"tier":"act","error":"required_by_dependency","blockedBy":[...
      component type names...]}` — detected proactively before attempting
      removal (`DestroyImmediate` on a required component logs a Console
      error and silently no-ops rather than throwing; relying on that would
      look like a false success over HTTP).
    - `/act/component/set-field`: field name doesn't exist on the component
      (or is `m_Script`, deliberately excluded from the read-side field
      vocabulary) → 400 `{"tier":"act","error":"unknown_field","field":...}`;
      field's `SerializedPropertyType` isn't one of the write-supported
      types (see `shared-helpers.md`'s `SerializedValueExtractor` entry for
      the full list) → 400
      `{"tier":"act","error":"unsupported_field_type","field":...,
      "propertyType":...}`; supplied JSON `"value"` doesn't match the
      field's expected shape (wrong primitive type, missing vector/color/
      rect component, unresolvable `ObjectReference` `assetGuid`/`objectId`,
      invalid enum name/index) → 400
      `{"tier":"act","error":"type_mismatch","detail":...}`.
  - **v2.5, the 5 prefab/transform endpoints only** (`Synchronous = true`,
    same `MutationRejection` mechanism as v2 above):
    - `/act/prefab/instantiate`: missing/empty `prefabGuid` → 400
      `{"tier":"act","error":"missing_prefab_guid"}`; guid doesn't resolve
      to a loadable `GameObject` asset, or resolves to something that isn't
      itself a prefab asset (`PrefabUtility.GetPrefabAssetType(...) ==
      PrefabAssetType.NotAPrefab`) → 400
      `{"tier":"act","error":"invalid_prefab_guid","prefabGuid":...}`.
    - `/act/transform/set`: none of `position`/`rotation`/`scale` supplied →
      400 `{"tier":"act","error":"no_fields"}`; a supplied field's shape
      doesn't match (missing a required component, e.g. rotation without
      `w`, or not a JSON object at all) → 400
      `{"tier":"act","error":"type_mismatch","detail":...}` — stricter than
      `create`/`instantiate`'s lenient per-component fallback (see
      `shared-helpers.md`'s `TransformParamReader` entry): a field you DO
      supply here must be fully specified.
    - `/act/prefab/save`: missing/empty `path` → 400
      `{"tier":"act","error":"missing_path"}`; path fails the reinstated
      self-protection check (outside `Assets/`, under the bridge's own
      package, or not ending in `.prefab`) → 400
      `{"tier":"act","error":"invalid_path","path":...}`; an asset already
      exists at `path` → 409
      `{"tier":"act","error":"asset_exists","path":...}` — no overwrite, no
      override flag.
    - `/act/prefab/apply`, `/act/prefab/revert` (shared via
      `PrefabConnectionResolver`): the resolved GameObject isn't a connected
      prefab instance (`PrefabUtility.GetPrefabInstanceStatus(...) !=
      PrefabInstanceStatus.Connected`) → 400
      `{"tier":"act","error":"not_prefab_instance"}`.

## `MiniJson.Write`'s type-conversion gotcha

`WriteValue`'s switch handles `null`, `bool`, `string`, `int`, `long`,
`float`, `double`, `IDictionary<string,object>`, `IEnumerable<object>` —
anything else falls through to `value.ToString()`, which is almost never
what you want for a Unity type (`Vector3.ToString()` → `"(1.0, 2.0, 3.0)"`,
not JSON; an enum's `.ToString()` happens to work but a `Color` or `Rect`
won't). **Always convert Unity structs to a `Dictionary<string,object>` (or
a primitive) yourself before handing a value to a response body** — see
`SerializedValueExtractor` in `shared-helpers.md` for the reference
implementation of this conversion for serialized component fields.

**v2 fix: non-finite `float`/`double` values are written as a quoted JSON
string, not a bare token.** JSON has no `Infinity`/`NaN` literal — the
naive `f.ToString(CultureInfo.InvariantCulture)` a plain numeric case would
use produces the bare token `Infinity`/`-Infinity`/`NaN` for a non-finite
value, which isn't valid JSON (`MiniJson.Parse` itself can't even read it
back). Found live via `HingeJoint.m_BreakForce`, whose real Unity default
is `float.PositiveInfinity` ("never breaks") — a pre-existing bug, not
introduced by v2, just never exercised by a component with a non-finite
default before. `WriteValue`'s `float`/`double` cases now check
`IsNaN`/`IsInfinity` first and route through `WriteString` (quoted) instead
of the bare-token path when true.
