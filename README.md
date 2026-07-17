# Unity Query Bridge

Editor-only local HTTP API exposing Unity project and scene information for
AI agent queries. Read-only in v1, zero player footprint.

Specs (LOCKED, live outside this repo):
- `C:\Users\DalyF\Desktop\Claude\Unity Bridge\unity-bridge-task-brief.md`
- `C:\Users\DalyF\Desktop\Claude\Unity Bridge\unity-bridge-human-verification.md`

Live build status: see the sandbox project's `TODO.md` at
`C:\Users\DalyF\Documents\GitHub\Unity MCP\TODO.md`.

## GATE 1 RESULTS

Not yet run. Phase 2 cannot start until this section contains a 10/10 pass
logged with date, Unity version, and 10 timing entries.

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
