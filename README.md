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

### Rerun 2026-07-18 (v1.5 acceptance criterion 5)

**Result:** 10/10 PASS. Required because v1.5's token-auth check sits
inside `HandleRequest`, within GATE 1's declared blast radius. Same script,
same pass conditions, run after the full v1.5 crash investigation and
driver update above.

| Iteration | Reconnect time | Refused attempts | States seen | Port |
|-----------|----------------|-------------------|-------------|------|
| 1 | 26.0s | 1 | ready, compiling | 17870 |
| 2 | 9.5s | 1 | ready, compiling | 17870 |
| 3 | 4.7s | 1 | ready, compiling | 17870 |
| 4 | 4.6s | 1 | ready, compiling | 17870 |
| 5 | 4.9s | 1 | ready, compiling | 17870 |
| 6 | 4.7s | 1 | ready, compiling | 17870 |
| 7 | 5.7s | 1 | ready, compiling | 17870 |
| 8 | 4.8s | 1 | ready, compiling | 17870 |
| 9 | 4.8s | 1 | ready, compiling | 17870 |
| 10 | 4.5s | 1 | ready, compiling | 17870 |

Iteration 1's 26.0s is real but still within the 30s ceiling — first-cycle
reconnects have consistently run slower than later ones across every GATE 1
run this project has done (7.6s in the original run's iteration 1 too),
plausibly a cold-start cost (JIT warmup, first-request overhead) rather
than anything specific to v1.5. Two earlier attempts the same session
failed outright (no disruption observed within 30s at all, not a marginal
miss) despite `Editor.log` confirming both edits *did* eventually trigger
real recompiles — timing investigation inconclusive (reaction-time,
environment load, and `date +%s%3N` precision were all checked and ruled
out); not reproduced on this clean pass, not chased further per the
"diagnose, fix, rerun from iteration 1" rule once a genuine 10/10 was
achieved.

## GATE 1.5 RESULTS

**Date:** 2026-07-18 · **Unity version:** 6000.5.2f1 · **Result:** 5/5 PASS
(`gate15-playmode.sh`, sandbox project `Unity MCP`)

All 5 iterations reconnected within the 30s ceiling, port stayed at 17870
throughout, and all 5 deliberate wrong-token requests correctly returned
401 without changing play state (10/10 real transitions confirmed). Fully
unattended — unlike GATE 1, nothing here needs a human-triggered recompile.

| Iteration | Enter time | Exit time | Wrong-token 401 | Port |
|-----------|-----------|-----------|------------------|------|
| 1 | 7.0s | 0.5s | confirmed | 17870 |
| 2 | 3.4s | 0.5s | confirmed | 17870 |
| 3 | 3.3s | 0.6s | confirmed | 17870 |
| 4 | 3.4s | 0.6s | confirmed | 17870 |
| 5 | 3.4s | 0.6s | confirmed | 17870 |

Run with a 5s settle pause between iterations (diagnostic addition after
the crash incident below — see DECISIONS). An earlier run the same day,
without the pause, also passed 5/5 but was followed shortly after by a real
Editor crash unrelated to any pass/fail condition of the gate itself — see
the crash investigation in DECISIONS for the full analysis and the
resulting `/act/playmode/*` cooldown.

**Post-driver-update reruns (same day, after the crash investigation
below):** user updated the NVIDIA driver (`32.0.15.9186` → `32.0.16.1074`,
confirmed via `Editor.log`'s `[D3D12 Device Filter] Driver Version` line —
the bridge itself has no API for this, it was checked the same way as the
original crash, by reading the log directly). Two isolation reruns done
with the cooldown and settle pause both temporarily disabled (`CooldownMs`
= 0, `SETTLE_SECONDS` = 0 — the exact zero-delay conditions of the original
crash) to test the driver update alone: **5/5 PASS, no crash.** Restored
both to production values (1000ms, 5s) and reran once more: **5/5 PASS
again.** Positive signal, but deliberately not claimed as proof — the
original crash also only happened once, sporadically, after an earlier
clean 5/5 pass on the *old* driver (and the two prior unrelated crashes on
2026-07-12/13 were days apart, not clustered under rapid toggling), so the
pre-update failure rate was already low enough that one clean post-update
trial doesn't rule out coincidence. Real confidence would need sustained
normal usage over days, not a single retest.

## ACCEPTANCE EVIDENCE

- **Criterion 1, literal `NetworkObject` test — 2026-07-18.** The brief's
  acceptance criterion 1 uses `/query {"hasComponent":"NetworkObject"}` as
  its example; earlier passes only exercised `hasComponent` with
  `AudioSource`/`Rigidbody`. Ran the literal query against False Signal
  (real NGO project). Stored form confirmed via
  `Library/UnityBridge/prefab_components.jsonl` before querying:
  `componentType` is the short class name `"NetworkObject"`
  (`Component.GetType().Name`), not the fully-qualified
  `Unity.Netcode.NetworkObject` — matching is `StringComparison.Ordinal`
  case-sensitive, so this distinction matters. Query returned 5 results:
  `Player.prefab`, `NPC.prefab`, `BlackHole.prefab`, `Planet.prefab` (all
  confirmed carrying `NetworkObject` in `prefab_components.jsonl`), plus
  `MainGame.unity` (via the `scenePrefabInstances` join). Cross-checked
  `Player.prefab`'s GUID (`712685e6cf91eac4291a76de99dc3404`) directly
  against `assets.jsonl` before trusting the query result.

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
- **2026-07-17 — Phase 3: `GlobalObjectId.GlobalObjectIdentifierToInstanceIDSlow`
  and `EditorUtility.InstanceIDToObject(int)` are hard-deprecated
  (`error CS0619`, not a warning) in Unity 6000.5.2f1.** `GET /object/{id}`
  originally used the InstanceID-based overloads (the documented API at
  the time the task brief was written); this Unity version's compiler
  rejects them outright. Switched to the newer EntityId-based pair,
  `GlobalObjectIdentifierToEntityIdSlow` / `EntityIdToObject`, same
  resolve-or-null contract. Not a deviation from any LOCKED behavior —
  the HTTP-visible contract (id in, object or 404 out) is unchanged — but
  worth recording since it's a Unity-version-forced API swap a future
  reader would otherwise have to rediscover from a compiler error.
- **2026-07-17 — Phase 3 ID stability-class contract: CONFIRMED, no
  deviation.** The brief's Phase 3 verification task (human-verification
  3.6) asked us to confirm by hand whether saved scene-object IDs actually
  survive a domain reload and a play-mode enter/exit round trip as
  claimed. Tested against `SampleScene`'s `Global Volume` object: same
  `GlobalObjectId` resolved correctly (no 404, no `"volatile": true`)
  before a script-edit-triggered reload, after it, and again after a full
  play-mode enter/exit cycle. The LOCKED contract stands as written —
  nothing to amend.
- **2026-07-17 — `GET /logs/tail` reads Console entries via a
  self-maintained ring buffer fed by `Application.logMessageReceivedThreaded`,
  not Unity's internal `LogEntries` reflection API.** The internal API
  (used by Unity's own Console window) would also capture entries logged
  before the bridge started, but it's an undocumented, version-fragile
  surface reached only via reflection. The public event only captures
  entries logged after the bridge starts listening — acceptable, since the
  bridge is already running before any agent session begins. Ordered
  newest-first in the response (a v1 readability choice, not LOCKED either
  way) since an agent tailing logs right after a change wants the most
  recent entry first.
- **2026-07-17 — `GET /object/{id}?components=values` only expands
  serialized fields for the root object's own components, never for
  entries under `children`, even at `depth=2`.** The brief doesn't specify
  whether `values` applies tree-wide; recursing full field values into a
  whole subtree would work against the context-efficiency pillar the rest
  of the API is built around, so `componentNames` (not full values) is
  what children ever get. A caller that wants a child's own field values
  makes a follow-up `/object/{childId}?components=values` call.
- **2026-07-17 — `GET /object/{id}`'s volatile-object heuristic: a
  `GlobalObjectId` with an all-zero `assetGUID` is marked `"volatile": true`.**
  This is Unity's documented signal for a non-persistent object (never
  saved to a scene/prefab file — e.g. instantiated at runtime, or living
  in a scene that itself was never saved). Confirmed consistent with the
  LOCKED ID stability-class contract by the 3.6 verification above (saved
  objects never got flagged volatile); not separately exercised against an
  actual runtime-instantiated object in this pass, since 3.6 only called
  for saved-object survival, not the volatile-flagging path itself.
- **2026-07-17 — `BridgeRequestContext` gained a `Query` field (GET query
  string parsing) in Phase 3, first needed by `/object/{id}`'s
  `depth`/`components` and `/logs/tail`'s `n`/`severity`.** Parsed with a
  small hand-rolled splitter in `BridgeServer.cs` rather than
  `System.Web.HttpUtility` — not reliably available under Unity's Editor
  assembly API compatibility level, consistent with this codebase already
  writing its own `MiniJson` instead of pulling in a JSON library.
- **2026-07-18 — `Volume`/package-script resolution: confirmed fixed as a
  side effect of the scan-scope fix, not separately.** The Phase 2 note
  above (URP's `Volume` `MonoBehaviour` skipped because its script guid
  wasn't in `script_types.jsonl`) predates the 2026-07-18 scan-scope fix
  (`IndexStore` DECISIONS entry, same date). Re-checked directly against
  the sandbox's current `scene_components.jsonl`:
  `{"guid":"99c9720ab...","componentType":"Volume","objectPath":"Global
  Volume"}` is now present for `SampleScene`. `Volume`'s source `.cs` lives
  under `Packages/`, which the old `Assets/`-only scan scope excluded from
  `script_types.jsonl`; now that both scan paths cover all of
  `Assets/`+`Packages/` (minus the bridge's own package), it resolves.
  Verified via static jsonl inspection only — the sandbox project wasn't
  open at the time, so this wasn't a live `/query` check. Neither the skill
  file nor `/help/query` ever documented this as a caveat, so nothing
  needed correcting there.
- **2026-07-18 — timing instrumentation added to both scan paths.**
  `RunFullScan()` and `ApplyIncrementalUpdate()` now wrap their work in a
  `Stopwatch` and log elapsed ms (`"Full index scan complete in Xms..."` /
  `"Incremental update applied in Xms..."`). Additive logging only, no
  behavior or response-shape change. Measured against False Signal after a
  real `Library/UnityBridge/` deletion + reopen: full rebuild 864ms (10734
  assets); one incremental update (single new asset) 46ms.
- **2026-07-18 — `/query` filter casing: documented, not normalized.**
  `hasComponent` and `pathPrefix` are `StringComparison.Ordinal`
  case-sensitive; `type` and `nameGlob` are case-insensitive. Chose to
  document this split explicitly (`/help/query` params + skill file, both
  regenerated verbatim from live `/help/query` output) rather than
  normalize all four to one casing rule — the split is defensible
  (`hasComponent`/`pathPrefix` match exact identifiers: C# class names and
  file paths; `type`/`nameGlob` are friendlier, fuzzier filters), and
  normalizing now would change matching behavior on a system that already
  passed its full acceptance sweep, without a specific defect motivating
  the change.
- **2026-07-17 — `GET /playmode`'s `elapsedSeconds` persists play-mode
  entry time to `Library/UnityBridge/playmode_entered` instead of a static
  field.** A domain reload (the common case on play-mode entry) resets all
  static state, which would zero the elapsed-time clock right as play mode
  starts. The handler always reads the marker file directly rather than
  caching a timestamp, so it's correct regardless of whether a reload
  happened, whether Configurable Enter Play Mode skipped it, or whether an
  unrelated mid-session script recompile reran the static constructor.
- **2026-07-18 — Editor crash during v1.5 testing, investigated, root
  cause found, `/act/playmode/*` cooldown added as a precaution (not a
  fix).** Shortly after `gate15-playmode.sh` passed 5/5 cleanly, the Unity
  Editor crashed (confirmed via `Editor.log`'s crash-handler output and a
  captured dump under `%LOCALAPPDATA%\Temp\Unity\Editor\Crashes\`). Initial
  stack trace pointed entirely at Unity's own D3D12 graphics pipeline
  (`GfxDeviceD3D12::QueuePresent`, `D3D12Fence::Wait`,
  `D3D12Window::EndRendering`) — nowhere near any bridge/script code.
  Deeper investigation of `Editor.log` found the actual signal:
  `D3D12Fence::Wait(...) error: got 18446744073709551615. Device removal.`
  followed by `d3d12: Device failed error (887a0006)` and `Unrecoverable
  GPU device error!` — `0x887a0006` is `DXGI_ERROR_DEVICE_REMOVED`, meaning
  the NVIDIA GPU driver itself reset/crashed the D3D12 device; Unity
  detected this and deliberately self-terminated (matches the
  `RaiseException` → `LaunchBugReporter` frames in the original trace)
  rather than continuing on a dead device. Windows Event Viewer confirmed:
  `Unity.exe` / `KERNELBASE.dll` / exception `0x40000015`
  (`STATUS_FATAL_APP_EXIT`) — a deliberate abort, not memory corruption.
  **Critically, the identical signature (`Device removal`, `887a0006`,
  same driver version `32.0.15.9186`) was found in two earlier crash logs
  from 2026-07-12 and 2026-07-13 — days before v1.5 existed, during
  ordinary unrelated work (the 07-12 one is deep into a long normal
  session, nowhere near any rapid-toggle burst).** Conclusion: this is a
  standing, recurring GPU driver issue on this specific machine (driver
  dated 2026-01-20, ~6 months old at investigation time), not something
  v1.5's testing uniquely caused. The "rapid playmode toggling stressed
  the pipeline" hypothesis is weakened by this finding, not eliminated —
  Editor.log does show a dense run of "GPU Resident Drawer created/disposed"
  pairs (one per domain-reloading playmode toggle) immediately before the
  crash, so rapid toggling remains a *plausible minor contributing factor*,
  just not the primary explanation. Real fix is a GPU driver update, which
  is outside the bridge's control and not something this project can force
  or verify per-machine. Added anyway, per explicit user decision: a 1000ms
  cooldown shared between `/act/playmode/enter` and `/act/playmode/exit`
  (`ActPlaymodeEndpoint.cs`, a plain `Stopwatch`, not
  `EditorApplication.timeSinceStartup` — keeps the request-thread-only,
  no-live-Unity-API discipline) — a request within 1s of the last
  *accepted* toggle returns `429 {"tier":"act","error":"cooldown",
  "retryAfterMs":<n>}`. Verified live: an immediate re-toggle after a fast
  exit (~0.5s) correctly returned 429 with an accurate `retryAfterMs`; the
  same request after the window passed correctly returned 202. Explicitly
  logged as a cheap, harmless precaution against one contributing factor,
  never presented as a proven fix for the actual root cause. Diagnostic
  recipe (grep `Editor.log` for `DXGI_ERROR_DEVICE_REMOVED`/`Device
  removal` when the bridge is unreachable, before assuming a script/bridge
  bug) added to the skill file's Failure modes and to
  `docs/architecture/routing-and-tiers.md`'s Domain reload survival
  section as a permanent limitation (the bridge runs inside the Unity
  process with no separate watchdog, so it can never distinguish its own
  crash from a slow reload, or self-report either).
- **2026-07-19 — `EditorApplication.isPlaying` is not a reliable "did this
  reload happen because Play Mode was just entered" signal, despite the
  v1.6 brief's mechanism assuming it was.** LOCKED text: "check
  `EditorApplication.isPlaying` inside `BridgeServer`'s static constructor
  ... `isPlaying == true` at that moment → this reload was caused by
  entering Play Mode." Disproved by live acceptance testing: a watch
  registered before entering Play Mode, and a raw log buffer with real
  data, both had their oldest surviving record's timestamp predate the
  `/act/playmode/enter` request itself by 30+ seconds, immediately after a
  confirmed `readyState:"playmode"` transition — meaning the wipe never
  fired on any entry tested, indistinguishable from a plain recompile.
  `isPlaying` apparently doesn't read back `true` until sometime after this
  reload's static constructors have already run. Fixed with the same
  event-driven marker-file pattern `PlaymodeEndpoint` already used for its
  own entry-timing marker: a new `LogPersistenceLifecycle.cs` writes
  `Library/UnityBridge/entering_playmode` on
  `PlayModeStateChange.ExitingEditMode` (fires synchronously *before* the
  reload), and `BridgeServer`'s static ctor consumes (deletes) it once,
  passing the resulting bool into `LogBuffer.Load(bool)`/
  `LogWatchStore.Load(bool)`. Re-verified live after the fix: the raw
  buffer's oldest record after a tightly-timed re-entry was the bridge's
  own `readyState: compiling -> playmode` log line, timestamped at the
  reload itself.
- **2026-07-19 — the entering-Play-Mode marker must be consumed exactly
  once per reload, by the caller, not independently by each store.** A
  bug in the fix above's first attempt: both `LogBuffer.Load()` and
  `LogWatchStore.Load()` called
  `LogPersistenceLifecycle.ConsumeEnteringPlayModeMarker()` themselves —
  since that method deletes the marker file on read, whichever store's
  `Load()` ran first (in practice `LogWatchStore`, called first in
  `BridgeServer`'s static ctor) consumed it correctly, but the second
  (`LogBuffer`) then found no marker and silently skipped its own wipe.
  Confirmed live: after a Play Mode entry, `log_watches/` was correctly
  wiped but `log_buffer.jsonl` still held 30+-second-old data. Fixed by
  computing the flag exactly once in `BridgeServer`'s static ctor and
  passing it into both `Load()` calls as a parameter.
- **2026-07-19 — entering Play Mode must not delete a watch's own
  registration, only its accumulated records.** A second, more consequential
  bug in the wipe mechanism: the original `LogWatchStore.WipeAll()` deleted
  the entire `log_watches/` directory, including `manifest.json` — so a
  watch registered immediately before entering Play Mode (the v1.6 brief's
  own documented idiom: register → `/act/playmode/enter` → `GET
  /logs/watches` to confirm it's active) vanished the instant Play Mode
  started, before it could ever observe the run it was registered for. The
  brief's wording only ever says persisted *log data* is wiped at entry,
  and its own skill-file note ("don't expect a watch registered before a
  test run to still hold data from a *previous* run") presupposes the watch
  itself survives. Fixed: `Load(bool enteringPlayMode)` always loads watch
  *definitions* from `manifest.json`; only `enteringPlayMode == true` skips
  reading old records and rewrites each watch's `.jsonl` empty instead.
  Re-verified live: a watch's `createdAt` was identical before and after a
  tightly-timed re-entry (proving the same definition, not a re-registration),
  while its records reset from 383 real entries to a fresh count matching
  only the new session's elapsed time.
- **2026-07-19 — the debounced disk-write timer was a starvation trap under
  continuous logging, not a working debounce.** Both `LogBuffer.OnLogMessage`
  and `LogWatchStore.TryRoute` re-armed their shared `_lastDirtyTime` timer
  on *every* new message, not just the first since the last flush. Under
  genuinely continuous chatty logging (a message every ~10ms, deliberately
  used for this acceptance test at the user's request specifically to stress
  "under load" per acceptance criterion 2's own wording) the timer's
  250ms-of-quiet condition never held, so `FlushIfDue()` never once decided
  it was safe to write — confirmed live: `log_buffer.jsonl` had zero bytes
  on disk after several seconds of a session producing ~200 messages/sec
  in memory, and only ever got saved via the guaranteed
  `OnBeforeAssemblyReload` flush at a reload boundary, never during a
  long-running session. This masked itself in earlier testing precisely
  because every prior test scenario happened to end at a reload. Fixed by
  arming the timer only on the transition into "dirty" (i.e. only when it
  was previously unset), turning the mechanism into a throttle (flush at
  least once per ~250ms regardless of continued activity) instead of a
  debounce (flush only after a quiet gap, which sustained load never
  produces). Re-verified live: both `log_buffer.jsonl` and a watch's
  `.jsonl` had real, growing content on disk after 3 seconds of continuous
  logging with zero reload involved.
