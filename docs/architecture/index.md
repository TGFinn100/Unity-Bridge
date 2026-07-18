# unity-bridge architecture reference — index

**What this is:** a structural reference to *how the code actually works* —
routing, tiers, response conventions, help generation, the index store, and
shared helpers. Written after Phase 3 so a future session (or agent) doesn't
have to re-derive it from scratch by reading every source file, the way
Phase 3 started.

**What this is not:** it doesn't duplicate the LOCKED specs
(`unity-bridge-task-brief.md` / `unity-bridge-human-verification.md`,
outside this repo), the package README's `DECISIONS` heading (deviations
and implementation notes, chronological), the README's `GATE 1 RESULTS`, or
the sandbox project's `TODO.md` (live phase/task status). Those are the
authoritative sources for spec, decisions, and status respectively — this
doc is purely "how the current code is structured," and should be updated
in place (not appended to) when that structure changes.

## How to use this

Read this index first. Then read **only** the topic file(s) relevant to
your task — each is self-contained and doesn't require the others for
context. Don't read the whole directory front-to-back unless you're doing a
full audit.

| File | Read this when... |
|---|---|
| [routing-and-tiers.md](routing-and-tiers.md) | You need to understand the request lifecycle: how a raw HTTP request becomes a handler call, the four tiers (meta/indexed/live/act) and why each dispatches the way it does, domain-reload survival, timeouts. |
| [response-envelope.md](response-envelope.md) | You're writing or debugging a response body: the `tier`/`indexedAt`/`frame`/`accepted`/`willReload` fields, the `truncated`/`total`/`hint` trio, error shapes for every status code (including the act-tier's 401/409/429/503), and `MiniJson`'s type-conversion gotcha. |
| [help-generation.md](help-generation.md) | You're adding an endpoint and need to know exactly which `EndpointInfo` fields make `/help` and `/help/{topic}` come out right. |
| [index-store.md](index-store.md) | You're touching anything under `Editor/Index/` — the jsonl schema, load/query/incremental-update flow, thread-safety model. |
| [shared-helpers.md](shared-helpers.md) | You need `ResponseCapping`, `BridgeState`, `MiniJson`, `ProjectPaths`, `LogBuffer`, `SerializedValueExtractor`, `ActionToken`, or `ActionScheduler` and want their contracts without re-reading each source file. |
| [adding-an-endpoint.md](adding-an-endpoint.md) | You're about to add a new endpoint. Start here — it's a checklist, not prose, and links back to the other files where a step needs more detail. |

## Maintenance rule

Update the relevant topic file **when the structure it describes actually
changes** — a new tier, a new shared helper, a change to the routing
algorithm, a new response-envelope convention. Don't update it for routine
new endpoints that follow the existing pattern (that's what
`adding-an-endpoint.md`'s checklist is for) or for anything that belongs in
`DECISIONS` instead (a one-off implementation choice or deviation, not a
structural pattern other endpoints will also follow).
