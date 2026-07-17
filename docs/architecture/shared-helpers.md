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

## `MiniJson.cs`

Hand-rolled compact JSON reader/writer — no external dependency, by
design (same reasoning that led to the hand-rolled query-string parser in
`BridgeServer.cs` rather than pulling in `System.Web`). `Parse()` is used
for POST bodies and for reading jsonl records back off disk; it only needs
to handle the subset `Write()` itself produces (no comments, no trailing
commas). See `response-envelope.md` for `Write()`'s type-dispatch gotcha.

## `ProjectPaths.cs`

`ProjectRoot`, `ToAbsolute(assetPath)`, `LibraryUnityBridgeDir` — the
single source of truth for "where does `Library/UnityBridge/` live."
Anything writing a bridge-owned file under `Library/` should go through
this rather than recomputing the path.

## `LogBuffer.cs` (Phase 3)

Bounded ring buffer (capacity 1000, `Queue<LogEntryRecord>`, one `lock`)
fed by `Application.logMessageReceivedThreaded` (subscribed once in
`BridgeServer`'s static constructor — may fire from any thread, hence the
lock). Deliberately **not** a reflection wrapper around Unity's internal
`LogEntries` API — no version-fragile internal surface to break across
Unity upgrades, at the cost of only capturing entries logged after the
bridge starts (acceptable: the bridge is already running before any agent
session begins). `Snapshot()` returns entries oldest-first; callers wanting
newest-first (e.g. `/logs/tail`) reverse after filtering/taking `n`.

## `SerializedValueExtractor.cs` (Phase 3)

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
object's own components, never recursively over a subtree — see
`ObjectEndpoint`'s depth-vs-values split in `adding-an-endpoint.md` if
extending this.
