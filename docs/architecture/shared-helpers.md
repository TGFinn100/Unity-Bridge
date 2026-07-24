# Shared helpers

Cross-cutting utilities used by more than one endpoint. If you need one of
these, use it as-is rather than re-deriving the logic locally.

## `ResponseCapping.cs`

`ApplyListCap(body, listKey, total, hint)` — the 16KB structural-truncation
contract. See `response-envelope.md` for the full contract and its two
known escape-hatch precedents (multi-list and nested-tree responses).

## `BridgeState.cs`

Thread-safe snapshot of Editor state, since the HTTP timeout path (meta and
the 504 body) runs on background threads that can't safely touch the Unity
API directly.

- `CachedReadyState` / `CachedUnityVersion` / `CachedProjectName` —
  `volatile string`, safe to read from any thread.
- `RefreshFromMainThread()` — called every `Tick()`; computes
  `readyState` as `compiling > indexing > playmode > ready` (first
  matching condition wins).
- `MarkCompiling()` / `MarkReloading()` — event-driven, called from
  `CompilationPipeline.compilationStarted` /
  `AssemblyReloadEvents.beforeAssemblyReload` respectively, **not** just
  the `Tick()` poll above. Necessary because a trivial single-file edit
  can trigger a Unity "forced synchronous recompile" that blocks
  `EditorApplication.update` for the entire compile+reload window — the
  poll alone could miss the transition completely if it never gets a
  chance to run before the state moves past it.
- `AddFrameIfPlaying(body)` (Phase 3) — adds `body["frame"] =
  Time.frameCount` when `EditorApplication.isPlaying`. Every live-tier
  handler calls this after building its response body.
- **v1.7:** `CachedCompileErrorCount` / `CachedCompileWarningCount`
  (`volatile int`) / `CachedCompileMessages` (`volatile
  IReadOnlyList<string>`, capped at `CompileMessageCap` = 20, errors always
  ordered before warnings) — Unity's own structured compiler output, not
  text scraped from `Editor.log`. `ResetCompileState()` is called from
  `BridgeServer.OnCompilationStarted`, the same moment `MarkCompiling()`
  fires. `AccumulateCompileMessages(CompilerMessage[])` is called from a new
  `CompilationPipeline.assemblyCompilationFinished` subscription
  (`BridgeServer.OnAssemblyCompilationFinished`) — fires once per assembly,
  so a single compile pass touching multiple assemblies calls this multiple
  times; counts always accumulate the true total, only the displayed
  message list is capped. `CompileMessagesAsObjectList()` converts the
  cached `List<string>` to a `List<object>` — required because
  `MiniJson.Write` only dispatches `IEnumerable<object>`, not
  `IEnumerable<string>` (see `response-envelope.md`'s `MiniJson.Write`
  gotcha) — shared by `/ping` and `/act/playmode/enter`'s
  `compile_errors_present` 409 body so both serialize the identical capped
  list via one code path.

## `MiniJson.cs`

Hand-rolled compact JSON reader/writer — no external dependency, by
design (same reasoning that led to the hand-rolled query-string parser in
`BridgeServer.cs` rather than pulling in `System.Web`). `Parse()` is used
for POST bodies and for reading jsonl records back off disk; it only needs
to handle the subset `Write()` itself produces (no comments, no trailing
commas). See `response-envelope.md` for `Write()`'s type-dispatch gotcha.

## `ActionToken.cs` (v1.5)

Per-project token for `/act/*` auth. `EnsureExists()` — called from
`BridgeServer`'s static constructor, pure file I/O safe before any
main-thread tick — generates a 32-byte random hex token on first run and
writes it atomically to `Library/UnityBridge/token`; stable across reloads
(never regenerated while the file exists) since it's read back off disk if
present. `IsValid(presented)` is a plain ordinal string comparison — no
timing-oracle mitigation, since the threat model is "another localhost
process/webpage guessing the port and firing a mutation," not secret
management for a real credential (same reasoning as v1's "no auth needed
while read-only" note).

## `GameObjectResolver.cs` (v2)

Shared id-resolution chain, extracted from `ObjectEndpoint` so the v2
mutation endpoints can reuse it verbatim (LOCKED, task brief "Object/
component resolution"): `ResolveOrThrow(id)` runs `GlobalObjectId.TryParse`
(400 on failure — the exact message `GET /object/{id}` already used) →
`GlobalObjectIdentifierToEntityIdSlow` → `EntityIdToObject` cast to
`GameObject` (404 `"stale id — ..."` if null, same message too). Also
carries `IsVolatile(GlobalObjectId)`, the all-zero-`assetGUID` check
(Phase 3). `ObjectEndpoint` itself was refactored to call this rather than
keep its own copy — no behavior change, just de-duplication.

## `ComponentSerializer.cs` (v2)

`ComponentNames(GameObject)` / `ComponentValues(GameObject)`, extracted from
`ObjectEndpoint`'s private methods of the same names — used by both
`ObjectEndpoint`'s node builder and `MutationNodeBuilder` (below) so the
single-node response shape stays identical in both places.

## `ComponentTypeResolver.cs` (v2)

`ResolveOrThrow(typeName)` for `/act/component/add`/`remove`/`set-field`:
searches every loaded assembly for a `Component`-derived type. Checks an
exact `Type.FullName` match first (can't be ambiguous, so a caller retrying
after a 400 `ambiguous_type` with a fully-qualified name is guaranteed to
resolve), then falls back to short `Type.Name` matching
(`StringComparison` via plain `==`, i.e. case-sensitive — matches `/query`'s
`hasComponent` casing convention). Zero short-name matches → 400
`unknown_component_type`; more than one → 400 `ambiguous_type` with every
candidate's `FullName`. Throws `MutationRejection` (see
`response-envelope.md`'s v2 error-shape list), not `BridgeHttpException` —
these need the richer `{"tier","error","type",...}` body, not the bare
`{"error": message}` shape.

## `MutationBody.cs` (v2)

Small shared POST-body readers for the mutation endpoints — `GetString`/
`GetBool` (plain `TryGetValue` + type-check, with a fallback for `GetBool`),
`ResolveIdOrThrow` (reads `"id"` and resolves it via
`GameObjectResolver.ResolveOrThrow` — a missing/empty value folds into that
resolver's own invalid-id 400 rather than a separate missing-field error),
and `ResolveOptionalParent` (distinguishes an omitted `"parent"` key from an
explicit JSON `null`, since "not sent at all" vs. "sent as null" mean
different things across create/duplicate/reparent's own defaults). Used by
all 13 synchronous mutation endpoints (the 8 v2 GameObject/component
endpoints and all 5 v2.5 prefab/transform endpoints) — every one needs at
least one of these.

## `MutationNodeBuilder.cs` (v2)

`BuildNode(GameObject)` — the single-node response shape shared by
`/act/gameobject/create`/`duplicate`/`reparent`/`rename`'s `"object"` field:
same shape as `GET /object/{id}?components=values`'s per-node dict
(`id`/`name`/`active`/`componentNames`/`components`/optional `volatile`),
built via `ComponentSerializer` + `GameObjectResolver.IsVolatile`, just
never nested (no `"children"` — no v2 endpoint takes a depth param).

## `MutationAutoSave.cs` (v2)

LOCKED "Auto-save" section: `Enabled` reads
`EditorPrefs.GetBool("UnityBridge.MutationAutoSave", true)` — default on.
The toggle is a Unity Editor menu item only (`Tools/Unity Bridge/Auto-Save
Mutations`, `[MenuItem]` + a validate function that also calls
`Menu.SetChecked` — the standard Unity idiom for a checkbox menu item, since
the validate function is what runs right before the menu is shown) — no
`/act` route reads or writes this value, deliberately keeping it outside
Claude's own reach as a genuine human safeguard. `SaveIfEnabled(GameObject)`
/ `SaveIfEnabled(Scene)` (the second overload exists for
`/act/gameobject/delete`, which must capture the scene *before* destroying
the object — nothing on a destroyed `UnityEngine.Object` is safe to read
afterward) — no-ops and returns `false` if the toggle is off; otherwise
`MarkSceneDirty` + `SaveScene` and returns `true`. Every mutation endpoint's
response includes this return value as `"autoSaved"`.

## `ActionScheduler.cs` (v1.5; signature extended v1.6; mutation slot added v2)

Single-pending-action guard + deferred main-thread execution, shared across
all `act`-tier endpoints. `Schedule(Func<Dictionary<string,object>,
ActBuildResult> buildAct, Dictionary<string,object> requestBody)` runs
entirely inside one lock: if an action is already pending, returns 409
`action_pending` without calling `buildAct` at all (LOCKED ordering —
pending-check strictly before idempotence-check, since computing
idempotence against a value that's about to change by the pending action is
pointless); otherwise calls `buildAct(requestBody)`, and if it returns a
202 stores its `MainThreadAction` as the pending action. `requestBody` was
added in v1.6 (previously a zero-arg `Func<ActBuildResult>`, called as
`buildAct()`) so `/act/logs/watch` can read `{name,pattern,capacity?}` from
the parsed POST body — `ActPlaymodeEndpoint`/`ActRefreshEndpoint` just
ignore the parameter. `RunPendingIfAny()` — called at the top of every
`Tick()`, before `IndexStore.RunFullScanIfNeeded()` — pops and invokes the
pending action (if any) on the main thread. This is what lets an `/act`
handler respond 202 on the request thread before the actual
`EditorApplication.isPlaying` toggle or `AssetDatabase.Refresh()` call runs.
See `routing-and-tiers.md`'s `act` tier section for the full request
lifecycle this participates in.

**v2:** `TryEnterMutation()`/`ExitMutation()` add a second exclusivity
mechanism under the same `_gate` lock — a `_mutationInFlight` bool, not a
deferred `Action` like `_pendingAction`, since a synchronous mutation has no
deferred step to store (see `routing-and-tiers.md`'s "Synchronous mutation
dispatch (v2)" section for the full request lifecycle). `Schedule()` now
also checks `_mutationInFlight` (409 `action_pending` if set), so an
existing act-tier call and a v2 mutation block each other in both
directions, not just mutation-vs-mutation.

## `ProjectPaths.cs`

`ProjectRoot`, `ToAbsolute(assetPath)`, `LibraryUnityBridgeDir` — the
single source of truth for "where does `Library/UnityBridge/` live."
Anything writing a bridge-owned file under `Library/` should go through
this rather than recomputing the path.

## `LogBuffer.cs` (Phase 3; persistence added v1.6)

Bounded ring buffer (capacity 1000, `Queue<LogEntryRecord>`, one `lock`)
fed by `Application.logMessageReceivedThreaded` (subscribed once in
`BridgeServer`'s static constructor — may fire from any thread, hence the
lock). Deliberately **not** a reflection wrapper around Unity's internal
`LogEntries` API — no version-fragile internal surface to break across
Unity upgrades, at the cost of only capturing entries logged after the
bridge starts (acceptable: the bridge is already running before any agent
session begins). `Snapshot()` returns entries oldest-first; callers wanting
newest-first (e.g. `/logs/tail`) reverse after filtering/taking `n`.

`OnLogMessage` first calls `LogWatchStore.TryRoute(message, time)` — a
message matching an active watch is condensed into that watch's own store
and never reaches `LogBuffer` at all (LOCKED "Raw-buffer overlap": diverted,
not double-counted).

**v1.6 persistence** (`Library/UnityBridge/log_buffer.jsonl`, previously
purely in-memory): `Load(bool enteringPlayMode)` — pure file I/O, called
from `BridgeServer`'s static constructor before any `Tick()`, with a flag
computed once by `LogPersistenceLifecycle.ConsumeEnteringPlayModeMarker()`
(see `routing-and-tiers.md`'s Domain reload survival section for why this
isn't `EditorApplication.isPlaying`, and why the flag is computed once by
the caller rather than by `LogBuffer` itself) — reads the file back on a
plain recompile/Play-Mode-exit reload, or deletes it when `enteringPlayMode`
is true.

Writes are throttled, not merely debounced: `OnLogMessage` sets an in-lock
`_dirty` flag and arms `_lastDirtyTime` **only on the transition into
dirty** (not on every message); `FlushIfDue()` (called every `Tick()`)
writes the whole snapshot via `JsonLinesIO.WriteLines` once ≥250ms has
passed since `_lastDirtyTime` was armed. Re-arming on every message instead
of only the first was a real bug caught during v1.6 acceptance testing
2026-07-19: under continuous chatty logging (a message every ~10ms) the
timer never went stale, so the "quiet long enough to flush" condition never
held and the on-disk file was never written except at a reload boundary —
confirmed live, a session with ~200 messages/sec had genuinely zero bytes
on disk after several seconds despite 1000 in-memory entries. `ForceFlush()`
— the authoritative dirty-check-and-write, checked/cleared entirely inside
the lock so a concurrent message is never silently dropped — is called
unconditionally from `OnBeforeAssemblyReload` (bypassing the debounce
window) so a message logged less than 250ms before a recompile still
reaches disk (LOCKED acceptance criterion 3: a plain recompile must not
lose data), and from `FlushIfDue()` once the throttle window has elapsed.

## `LogWatchStore.cs` (v1.6)

Named, condensed log watches — `Editor/LogWatchStore.cs` (flat under
`Editor/`, alongside `LogBuffer.cs`, not under `Editor/Index/`; unrelated
subsystem despite the superficial jsonl-persistence similarity to
`IndexStore`). One `Dictionary<string, WatchEntry>` (name → definition +
compiled `Regex` + a per-watch `Queue<LogWatchRecord>` ring buffer trimmed
to that watch's `capacity`), one lock guarding all of it — matches can
arrive on any thread via `LogBuffer.OnLogMessage`.

- **`TryRoute(message, time)`** — tries every active watch's regex against
  the message; on a match, builds a `values` dict from the regex's *named*
  capture groups only (`GetGroupNames()` filtered to skip the numeric
  auto-groups), enqueues a `LogWatchRecord`, marks that watch dirty. Returns
  whether anything matched (see `LogBuffer.OnLogMessage` above). Arms
  `_lastDirtyTime` only on the transition into dirty, same throttle-not-
  debounce fix as `LogBuffer.OnLogMessage` — see that entry above for the
  bug this avoids.
- **`Register`/`Unregister`** — the deferred `MainThreadAction` behind
  `POST /act/logs/watch`/`/act/logs/unwatch` (see `routing-and-tiers.md`'s
  `act` tier section). Writes `log_watches/manifest.json` (all active
  definitions, atomic temp+replace) and that watch's own
  `log_watches/<name>.jsonl`; `Unregister` also deletes the `.jsonl` (LOCKED:
  "no discovery path remains to an orphaned file").
- **`FlushIfDue()`/`ForceFlush()`** — same throttle-plus-forced-flush-at-
  reload-boundary pattern as `LogBuffer`, but per-watch: `ForceFlush()`
  scans every watch's `Dirty` flag inside the lock (not an external
  "anything pending" flag) so a match concurrent with a flush is never lost,
  and only rewrites the `.jsonl` files that actually changed.
- **`Load(bool enteringPlayMode)`** — watch *definitions* are always loaded
  from `manifest.json` regardless of `enteringPlayMode`; only each watch's
  *records* are affected — `enteringPlayMode == true` starts each with an
  empty in-memory queue and rewrites its `.jsonl` empty, instead of reading
  the old records back. A second real bug caught during v1.6 acceptance
  testing 2026-07-19: the original version deleted the entire
  `log_watches/` directory (including `manifest.json`) on entry, so a watch
  registered right before entering Play Mode — the LOCKED brief's own
  documented idiom — vanished the instant Play Mode started, before it
  could ever observe the run it was registered for. A corrupt manifest
  entry (bad regex, unparsable jsonl) is skipped with a logged warning
  rather than discarding every other watch — same self-heal spirit as
  `IndexStore.Load()`.
- **`CompileValidate(pattern)`** — just `new Regex(pattern)`; callers (only
  `ActLogsWatchEndpoint`) catch the exception and turn it into 400
  `invalid_pattern`. Pure regex compilation, no Unity API, safe on any
  thread — this is what makes "validated at registration time" (LOCKED)
  possible without deferring to the main thread.

## `LogPersistenceLifecycle.cs` (v1.6)

The reliable "did this reload happen because Play Mode was just entered?"
signal `LogBuffer.Load()`/`LogWatchStore.Load()` both need, replacing the
v1.6 brief's original (disproved) `EditorApplication.isPlaying`-at-ctor-time
mechanism — full incident in `routing-and-tiers.md`'s Domain reload
survival section. Event-driven, mirroring `PlaymodeEndpoint`'s own
entry-timing marker: `OnPlayModeStateChanged` (subscribed independently
from `BridgeServer`'s static ctor, alongside `PlaymodeEndpoint`'s own
subscription to the same event) writes a marker file
(`Library/UnityBridge/entering_playmode`) on
`PlayModeStateChange.ExitingEditMode`, which fires synchronously *before*
the domain reload that follows an entry into Play Mode.
`ConsumeEnteringPlayModeMarker()` checks for and deletes the marker,
returning whether it was present — called **exactly once**, from
`BridgeServer`'s static constructor, with the resulting bool threaded into
both `Load()` calls. Do not call `ConsumeEnteringPlayModeMarker()` from
either store directly: it consumes (deletes) the marker on read, so two
independent callers in the same static-ctor run would mean only the first
one ever sees it — this was itself a real bug caught during v1.6 acceptance
testing, see the method's own doc comment.

On-disk layout, all under `Library/UnityBridge/log_watches/`:
`manifest.json` (`{watches:[{name,pattern,capacity,createdAt}]}`) and one
`<name>.jsonl` per watch (`{values:{...named groups...},time}` per line,
ring-buffer order). `GET /logs/watches` (meta tier) and `GET
/logs/watch/{name}` (live tier — not in the original v1.6 task brief; added
after a real gap was found and flagged to the user, see that endpoint file's
own header comment) are the only sanctioned read paths — same "don't read
`Library/UnityBridge/` files directly" rule as the index store's
`meta.json`.

## `SerializedValueExtractor.cs` (Phase 3; Quaternion support added v2.5)

Converts a `Component`'s serialized fields to JSON-safe primitives, for
`GET /object/{id}?components=values`. Walks via `SerializedObject.GetIterator()`
+ `NextVisible(enterChildren)`, with `enterChildren = true` **only on the
first call** and `false` thereafter — this is the standard Unity idiom for
"iterate top-level fields only," and is what stops a `Vector3` or `Color`
field from exploding into separate `x`/`y`/`z`/`r`/`g`/`b`/`a` entries
(those compound types expose their value directly via
`.vector3Value`/`.colorValue` instead). Switches on `SerializedPropertyType`
for primitives/`Vector2-4`/`Color`/`Enum`/`ObjectReference`/etc.; anything
unhandled (arrays, generic nested types, gradients, curves) collapses to a
`"<PropertyType>"` placeholder rather than attempting a full recursive
serializer — deliberate, per the context-efficiency pillar (an unbounded
array could blow a response on its own). Only ever called for a single
object's own components, never recursively over a subtree — see the two
nested-shape precedents (including `ObjectEndpoint`'s depth-vs-values
split) in `response-envelope.md`'s "The truncated/total/hint trio" section
if extending this.

**v2.5:** `SerializedPropertyType.Quaternion` added to the switch, encoded
as `{x,y,z,w}` via `.quaternionValue` (the raw components, not Euler) —
the foundational fix the whole v2.5 slice depends on. `/act/component/
set-field`'s `WriteValue` switch (`ComponentSetFieldEndpoint.cs`) mirrors
this on the write side, requiring all four components (unlike Vector3/
Vector4's optional trailing components — a partial quaternion isn't a
meaningful value). This also migrated `/act/gameobject/create`'s
`rotation` param from Euler `{x,y,z}` to Quaternion `{x,y,z,w}` — see
`TransformParamReader.cs` below.

## `TransformParamReader.cs` (v2.5)

`ReadVector3(body, key, fallback)` / `ReadQuaternion(body, key, fallback)`
— shared position/rotation/scale body-reading, extracted from
`GameObjectCreateEndpoint` (v2) so `/act/prefab/instantiate` (v2.5) reuses
the identical defaults and per-component-fallback behavior instead of
duplicating it. Lenient: a partial value (e.g. only `x`/`y` supplied) fills
its missing components from `fallback` rather than rejecting the request —
`TransformSetEndpoint` (below) deliberately does **not** use this helper,
since it requires strict all-or-nothing validation on any field it's
actually given (a supplied-but-partial field is a caller error there, not a
partial-update request).

## `PrefabConnectionResolver.cs` (v2.5)

`RequireConnectedInstance(GameObject, out prefabGuid, out prefabPath)` —
shared "is this a connected prefab instance" check + nearest-source lookup,
used by both `/act/prefab/apply` and `/act/prefab/revert` (LOCKED: revert's
`400 not_prefab_instance` is "same check as apply"). Throws
`MutationRejection(400, {"error":"not_prefab_instance"})` if
`PrefabUtility.GetPrefabInstanceStatus(go) != PrefabInstanceStatus.Connected`.
Otherwise resolves the nearest corresponding source via
`PrefabUtility.GetCorrespondingObjectFromSource` — for a Prefab Variant
instance this is the variant itself, not the ultimate base further up the
chain, matching `ApplyPrefabInstance`'s own "applies to the immediate
source" behavior (confirmed empirically during build, per the brief's own
deferral). Only `apply` uses the returned `prefabGuid`/`prefabPath` (for its
`"applied"` response field); `revert` discards both via `out _, out _`.
