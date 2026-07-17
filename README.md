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
