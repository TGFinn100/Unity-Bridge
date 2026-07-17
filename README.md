# Unity Query Bridge

Editor-only local HTTP API exposing Unity project and scene information for
AI agent queries. Read-only in v1, zero player footprint.

Specs (LOCKED, live outside this repo):
- `C:\Users\DalyF\Desktop\Claude\Unity Bridge\unity-bridge-task-brief.md`
- `C:\Users\DalyF\Desktop\Claude\Unity Bridge\unity-bridge-human-verification.md`

Live build status: see the sandbox project's `TODO.md` at
`C:\Users\DalyF\Documents\GitHub\Unity MCP\TODO.md`.

## GATE 1 RESULTS

**Date:** 2026-07-17 · **Unity version:** 6000.5.2f1 · **Result:** 10/10 PASS
(`gate1-torture.sh`, sandbox project `Unity MCP`)

All 10 reconnects completed within the 30s ceiling, bound port stayed at
17870 throughout, `Library/UnityBridge/port` matched the live port after
every reconnect, and `readyState: "compiling"` was observed on every single
iteration (see DECISIONS below for what it took to make that reliably
observable).

| Iteration | Reconnect time | Refused attempts | States seen | Port |
|-----------|----------------|-------------------|-------------|------|
| 1 | 7.6s | 1 | ready, compiling | 17870 |
| 2 | 4.3s | 1 | ready, compiling | 17870 |
| 3 | 4.2s | 1 | ready, compiling | 17870 |
| 4 | 4.3s | 1 | ready, compiling | 17870 |
| 5 | 3.9s | 1 | ready, compiling | 17870 |
| 6 | 3.8s | 1 | ready, compiling | 17870 |
| 7 | 3.8s | 1 | ready, compiling | 17870 |
| 8 | 3.8s | 1 | ready, compiling | 17870 |
| 9 | 3.8s | 1 | ready, compiling | 17870 |
| 10 | 3.8s | 1 | ready, compiling | 17870 |

Two earlier full 10-iteration runs the same day did not pass and are not
counted here — one before `gate1-torture.sh`'s own reconnect-timing logic
was fixed (started its clock after the human-alt-tab pause instead of
immediately, so it mostly observed an already-finished reload), one after
that fix but before the `compiling`-observability fix below (state was
tracked correctly internally but never delivered over HTTP). See DECISIONS.

## DECISIONS

- **2026-07-17 — commit-tracker hook uses mtime comparison, not `git status`.**
  Squelch's original `check-todo-before-commit.py` checks `git status` on
  `TODO.md` because that file lives inside the same repo being committed to.
  Here, `TODO.md` lives in the sandbox project
  (`C:\Users\DalyF\Documents\GitHub\Unity MCP`), which is explicitly **not**
  a git repo, while commits happen in this package repo. `git status` can't
  track a file outside any working tree. The hook instead stores TODO.md's
  last-modified timestamp in `.claude/hooks/.todo-mtime-state.json` after
  each commit and warns on the next commit if the mtime hasn't advanced.
  Chosen by the user over git-initializing the sandbox or dropping the check.
- **2026-07-17 — hook-firing verification pending.** Phase 0 and Phase 1
  commits both went through without `.claude/hooks/.todo-mtime-state.json`
  being created, meaning the PreToolUse hook never actually ran for either.
  Running the prescribed real check now: commit in this session, confirm the
  state file appears.
- **2026-07-17 — `readyState: "compiling"` needed an event-driven trigger,
  not just the `Tick()` poll.** `BridgeState.RefreshFromMainThread()` (run
  every `EditorApplication.update`) checked `EditorApplication.isCompiling`,
  but for a trivial single-comment edit Unity performs a "forced synchronous
  recompile" (confirmed in `Editor.log`) that blocks the main thread — and
  therefore `EditorApplication.update` — for the entire compile+reload. That
  window could close before any `Tick()` ever ran, so `"compiling"` was never
  set at all (confirmed absent from `Editor.log` across 26 reload cycles
  before the fix). Fixed by subscribing to
  `UnityEditor.Compilation.CompilationPipeline.compilationStarted`, which
  fires synchronously the instant compilation begins, and calling a new
  `BridgeState.MarkCompiling()` from it — independent of `Tick()`'s cadence.
- **2026-07-17 — meta-tier endpoints (`/ping`, `/help`) bypass the
  main-thread request queue; deviates from the original Phase 1 note that
  "all requests route through the main-thread queue in v1."** Even after the
  fix above made `BridgeState`'s internal `readyState` correctly transition
  through `"compiling"`, no HTTP client could ever observe it: `/ping`
  responses are only produced by `Tick()` draining `_queue`, and `Tick()`
  itself can't run during the same main-thread-blocking synchronous
  recompile described above. Any request arriving mid-compile just sat
  queued until the reload finished and `Tick()` resumed — by which point the
  state had already moved past `"compiling"` to `"ready"`. This is a real gap
  against the task brief's own requirement that `"compiling is served by the
  old domain while compilation runs."` Fixed by answering `Tier == "meta"`
  endpoints directly on the background request thread (`HandleRequest`),
  bypassing `_queue`/`Tick()` entirely — safe because `/ping` and `/help`
  only read pre-cached, thread-safe (`volatile` or write-once-at-startup)
  state and never call into the Unity API. Indexed/live-tier endpoints
  (Phase 2+) still route through the main-thread queue, since those *do*
  need main-thread Unity API access — the original Phase 1 note was correct
  for that case, just not for meta-tier. Confirmed via `Editor.log`:
  `"compiling"` now observed via `/ping` on 10/10 GATE 1 iterations.
- **2026-07-17 — correction to the note directly above: indexed-tier
  endpoints do NOT route through the main-thread queue after all.** The
  task brief's own "Domain reload survival" section (LOCKED) says indexed
  reads may answer directly from the index store on the listener thread,
  with a 1s timeout specifically because "no main thread involved — slow
  means broken, fail fast." That detail wasn't accounted for when the Phase
  1 note above was written. `POST /query` and `GET /asset/{guid}`
  (Phase 2) answer directly on the request thread exactly like meta-tier,
  gated on `IndexStore.IsReady` (503 + `readyState` when not ready, per the
  same LOCKED section). The index store itself — not the dispatch — is what
  needed to become thread-safe: a single `lock` around its in-memory lists,
  since full scans and incremental updates run on the main thread
  (`AssetPostprocessor` / `Tick()`) while reads happen on background
  request threads.
- **2026-07-17 — asset `type` string taxonomy for `/query` is a v1 choice,
  not a locked contract.** The brief only requires `"prefab"` to work
  (verification 2.1, 2.5). Classified by file extension in
  `AssetTypeClassifier`: `.prefab`→prefab, `.unity`→scene, `.cs`→script,
  `.mat`→material, `.asset`→scriptableobject, common image/audio/model
  extensions→texture/audioclip/model, folders→folder, else→other.
- **2026-07-17 — scene YAML override-ADDED component objectPath is
  approximated, not exact.** `SceneYamlParser` resolves components added to
  a prefab instance in-scene (their `m_GameObject` points at a "stripped"
  GameObject proxy owned by a `PrefabInstance`) by attributing them to the
  *instance's own resolved path*, not their exact nested position inside
  the prefab — resolving that precisely would require parsing the source
  prefab's own hierarchy and matching `m_CorrespondingSourceObject` fileIDs
  against it. Native scene components and the `PrefabInstance` →
  `sourcePrefabGuid` join (used by `/query`'s `hasComponent`) are exact.
  The brief's pre-approved escape hatch (fall back to native+join only,
  drop added-component detection) was not needed — 2.1–2.5 passed against
  the sandbox's real `SampleScene.unity` (including a URP `Global Volume`
  object whose `Volume` MonoBehaviour component is correctly skipped, since
  its script guid isn't in `script_types.jsonl` — that file only covers
  project `.cs` files, not package scripts — bounded semantics working as
  documented, not a bug).
- **2026-07-17 — `/asset/{guid}` on a scene asset returns `components`
  (native scene_components) and `prefabInstances` as separate lists rather
  than pre-flattening them into one merged tree.** `/query`'s
  `hasComponent` already performs the join for search; a caller wanting a
  specific instance's full effective component list can follow up with
  `/asset/{guid}` on that instance's `sourcePrefabGuid`. Not otherwise
  exercised by 2.1–2.5, which test `/query`, not `/asset/{guid}`'s scene
  branch specifically.
- **2026-07-17 — jsonl files must be written without a UTF-8 BOM.**
  `File.WriteAllText(path, contents, Encoding.UTF8)` writes a byte-order
  mark; the parameterless-encoding overload (used for `meta.json` and the
  port file) doesn't. First-pass `JsonLinesIO.WriteLines` used the
  BOM-writing overload, producing a stray invisible byte before the first
  `{` in `assets.jsonl`/`scene_components.jsonl` — harmless to Unity's own
  reader (`File.ReadAllLines` auto-detects and strips a leading BOM
  regardless of the encoding passed) but works against the LOCKED
  "grep/diff-friendly" design goal for a naive line-based tool. Fixed with
  a shared `new UTF8Encoding(false)` constant.
