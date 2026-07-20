# Routing and tiers

Two files own this: `Editor/EndpointRegistry.cs` (the data model + router)
and `Editor/BridgeServer.cs` (the listener, dispatch, and domain-reload
lifecycle). There is no separate "dispatcher" class beyond these two.

## The data model (`EndpointRegistry.cs`)

- `EndpointInfo` — one per endpoint: `Method`, `Path` (display only for
  topic routes), `TopicKey`, `Tier` (`"meta"|"indexed"|"live"|"act"`),
  `Summary` (≤8 words), `Params`, `ExampleRequest`, `ExampleResponseAbbrev`,
  `TimeoutMs`, `IsTopicRoute`/`ParamPrefix`, `Handler` (meta/indexed/live
  only), `BuildAct` (act tier only — see the `act` tier section below).
  **v1.6:** `BuildAct`'s signature is `Func<Dictionary<string,object>,
  ActBuildResult>`, not the zero-arg `Func<ActBuildResult>` v1.5 shipped
  with — it now receives the parsed POST body (null if none), added so
  `/act/logs/watch` can read `{name,pattern,capacity?}`.
  `ActPlaymodeEndpoint`/`ActRefreshEndpoint` simply ignore the parameter,
  same as any handler ignoring an unused arg.
- `BridgeRequestContext` — passed to every handler: `Topic` (captured
  path-param segment), `Body` (parsed POST JSON, null if none), `Query`
  (parsed GET query string, added Phase 3 — never null, empty dict if
  none).
- `BridgeHttpException(statusCode, message)` — throw this from a handler
  to produce a specific status + `{"error": message}` body instead of the
  default 500.
- `EndpointRegistry.Resolve(method, path, out topic)` — the router. Two
  passes: exact `Method`+`Path` match first (ignoring topic routes), then
  a prefix match against `IsTopicRoute` entries' `ParamPrefix` (e.g.
  `/asset/`), capturing everything after the prefix as `topic`. Returns
  `null` on no match → caller writes 404.
- `EndpointRegistry.FindByTopic(topicKey)` — separate lookup used only by
  `/help/{topic}`, keyed on `TopicKey` (not the same thing as route
  matching above).

## Registering an endpoint

Every endpoint file has a static `Register()` that builds one `EndpointInfo`
and calls `EndpointRegistry.Add(info)`. All `Register()` calls are invoked
from `BridgeServer.RegisterEndpoints()` — the **only** place a new endpoint
needs manual wiring beyond its own file. See `adding-an-endpoint.md`.

## Request lifecycle (`BridgeServer.HandleRequest`)

1. Host-header check (must be `localhost`/`127.0.0.1` — DNS-rebinding
   hygiene) → 403 otherwise.
2. Parse `method`, `path` (`Url.AbsolutePath`), `query` (`Url.Query`, via a
   hand-rolled parser — no `System.Web.HttpUtility` dependency).
3. `EndpointRegistry.Resolve` → 404 `{"error":"unknown endpoint — see /help"}`
   if nothing matches.
4. Parse POST body as JSON via `MiniJson.Parse` if present → 400 on
   malformed JSON.
5. Branch on `endpoint.Tier`.

## The four tiers

**`meta`** (`/ping`, `/help`, `/help/{topic}`, `/logs/watches` — v1.6) — answered directly on the
background request thread, bypassing the main-thread queue entirely.
Deliberate: Unity can run a "forced synchronous recompile" that blocks
`EditorApplication.update` (and therefore `Tick()`) for the whole
compile+reload window. If meta requests went through the queue, `/ping`
could never report `readyState:"compiling"` during exactly the window
that matters. Meta handlers may only read pre-cached, thread-safe state — `BridgeState`'s
`volatile` fields for most (`/ping`), but a handler may instead guard its
own state with a dedicated lock, as `/logs/watches` does via
`LogWatchStore`'s internal `_gate` (v1.6). Either way, never call into the
live Unity API directly. `TimeoutMs` in practice: 5000.

**`indexed`** (`/query`, `/asset/{guid}`) — also answered directly on the
request thread (LOCKED per the task brief: "no main thread involved — slow
means broken, fail fast"), gated on `IndexStore.IsReady` (503
`{"tier":"indexed","error":"indexing","readyState":...}` if not). Thread
safety comes from `IndexStore`'s own internal lock, not from this dispatch.
`TimeoutMs` in practice: 1000 (fail-fast, per the LOCKED rationale above).

**`live`** (`/scene/summary`, `/object/{id}`, `/logs/tail`, `/playmode`,
`/logs/watch/{name}` — v1.6) —
the fallthrough case: everything that isn't `meta` or `indexed` routes
through the `ConcurrentQueue<PendingRequest>` + `EditorApplication.update`
dispatcher (`Tick()`). The request thread enqueues a `PendingRequest`
(carrying `Topic`/`Body`/`Query`/a `TaskCompletionSource`) and blocks on
`Tcs.Task.Wait(endpoint.TimeoutMs)`; `Tick()` drains the queue on the main
thread, safe to call any Unity API. On timeout, the request thread returns
504 `{"tier","error":"timeout","readyState","elapsedMs"}` — the pending
item still gets drained eventually, its result just gets discarded (no one
is waiting). `TimeoutMs` in practice: 5000. **A new endpoint that needs
real Unity API access just sets `Tier = "live"` — no `BridgeServer` change
is needed**, this path already exists and is exercised by all four Phase 3
endpoints.

**`act`** (`/act/playmode/enter`, `/act/playmode/exit`, `/act/refresh` —
v1.5; `/act/logs/watch`, `/act/logs/unwatch` — v1.6) — mutating endpoints,
dispatched entirely differently from the three read-only tiers above. An
`act` endpoint sets `BuildAct` (a `Func<Dictionary<string,object>,
ActBuildResult>` — see the `EndpointInfo` note above) instead of `Handler`,
and `HandleRequest` branches on `endpoint.Tier == "act"` before it ever
reaches the main-thread queue:

1. **Token check first (LOCKED ordering)** — `X-Bridge-Token` header
   checked against `ActionToken.IsValid`; invalid/missing → unconditional
   401 `{"error":"unauthorized","tier":"act"}`, before readyState or any
   other check runs, so an unauthenticated caller can't infer state via a
   409/503 that would otherwise fire first.
2. **Readiness gate** — `BridgeState.CachedReadyState` must be `"ready"`
   or `"playmode"`; anything else (compiling/indexing/reloading) → 503
   `{"tier":"act","error":"not_ready","readyState":...}`. An action is
   never queued across a reload.
3. **`ActionScheduler.Schedule(endpoint.BuildAct, parsedBody)`** — runs the
   endpoint's `BuildAct(parsedBody)` under `ActionScheduler`'s single lock.
   `BuildAct` only reads pre-cached thread-safe state (same discipline as
   every other tier) and returns an `ActBuildResult`: either a 409 conflict
   (e.g. `already_in_state`), a 400/404 validation failure (v1.6:
   `/act/logs/watch`'s missing-field/invalid-regex checks,
   `/act/logs/unwatch`'s unknown-name check) with no action, or a 202
   `{"accepted":true,...}` plus an `Action` deferred to the next `Tick()`.
   If another action is already pending, `Schedule` returns 409
   `{"tier":"act","error":"action_pending"}` without calling `BuildAct` at
   all (LOCKED ordering — pending-check strictly before idempotence-check).
   `ActionScheduler.RunPendingIfAny()` runs the deferred `Action` on the
   main thread at the start of the next `Tick()`, before
   `IndexStore.RunFullScanIfNeeded()`.

**Accepted-then-reconnect contract (LOCKED):** the 202 response is sent
*before* the actual state change runs — entering/exiting play mode or
refreshing the AssetDatabase typically triggers a domain reload that tears
down the listener, so there's no way to respond *after* the change without
racing the reload. Callers treat a 202 as "the action was scheduled;
reconnect and poll `/ping` to observe the result," never as confirmation
the change already happened.

**v1.6 exception:** `/act/logs/watch`/`/act/logs/unwatch` also defer their
mutation to the next `Tick()` (same `ActBuildResult`/`MainThreadAction`
plumbing, for consistency — not because the file I/O needs the main thread)
but never trigger a domain reload, so their 202 body always carries
`"willReload":false`. The accepted-then-*poll* half of the contract still
holds — a caller confirms the watch is live via `GET /logs/watches` rather
than a reconnect.

`ActPlaymodeEndpoint` additionally layers a 1000ms cooldown shared between
enter/exit, checked before the idempotence check: a toggle within the
window of the last accepted one → 429
`{"tier":"act","error":"cooldown","retryAfterMs":...}`. **v1.7:**
`BuildEnter` (enter only, not exit) layers one more check, after the
idempotence check and before scheduling — `BridgeState.CachedCompileErrorCount
> 0` → 409 `{"tier":"act","error":"compile_errors_present",...}`, action
never scheduled. Full LOCKED order for `/act/playmode/enter`: cooldown →
already_in_state → compile_errors_present → schedule 202. See
`shared-helpers.md` for `ActionToken.cs`, `ActionScheduler.cs`, and
`BridgeState`'s v1.7 compile-cache fields.

`TimeoutMs` in practice: 5000 (unused in the request-thread-direct dispatch
above, kept for `EndpointInfo` shape consistency with the other tiers).

## Domain reload survival

`[InitializeOnLoad]` on `BridgeServer` re-runs its whole static constructor
after every domain reload — all static state (the queue, cached endpoint
list, etc.) resets for free. The constructor also subscribes:
`CompilationPipeline.compilationStarted` → `BridgeState.MarkCompiling()` +
**(v1.7) `BridgeState.ResetCompileState()`**,
`CompilationPipeline.assemblyCompilationFinished` → **(v1.7)
`BridgeState.AccumulateCompileMessages(messages)`** — fires per-assembly
with that assembly's own `CompilerMessage[]`, the structured error/warning
signal surfaced on `/ping` and checked by `/act/playmode/enter` (see
`shared-helpers.md`'s `BridgeState` entry and `response-envelope.md`'s
field/error-shape additions),
`AssemblyReloadEvents.beforeAssemblyReload` → **(v1.6) `LogBuffer.ForceFlush()`
+ `LogWatchStore.ForceFlush()`, then** `BridgeState.MarkReloading()` + stop
the listener (clean socket teardown — this is what makes GATE 1's "no
address-in-use" requirement hold), `EditorApplication.quitting` → stop
listener, `EditorApplication.playModeStateChanged` →
`PlaymodeEndpoint.OnPlayModeStateChanged` (Phase 3, entry-time marker file)
**and (v1.6) `LogPersistenceLifecycle.OnPlayModeStateChanged`** (a second,
independent subscriber to the same event — see below),
`Application.logMessageReceivedThreaded` → `LogBuffer.OnLogMessage` (Phase
3, console ring buffer; v1.6: now also consults `LogWatchStore.TryRoute`
first — see `shared-helpers.md`). None of these need explicit
unsubscription — a domain reload discards the old assembly (and every
delegate pointing into it) wholesale.

**v1.6:** the constructor also calls
`LogPersistenceLifecycle.ConsumeEnteringPlayModeMarker()` exactly once,
then passes the resulting bool to both `LogWatchStore.Load(bool)` and
`LogBuffer.Load(bool)` (pure file I/O, same "safe before any Tick()"
discipline as `IndexStore.Load()`/`ActionToken.EnsureExists()` right above
them). `true` means this reload was caused by entering Play Mode, so both
stores start their records fresh (a watch's own *definition* survives —
only its accumulated records reset, see `shared-helpers.md`).

This does **not** check `EditorApplication.isPlaying` — the original v1.6
brief's assumed mechanism, disproved by live acceptance testing 2026-07-19:
`isPlaying` doesn't read back `true` at static-ctor time during an
entry-triggered reload, making it indistinguishable from a plain recompile
or an exit reload. `LogPersistenceLifecycle` uses the same event-driven
marker-file pattern `PlaymodeEndpoint` already relies on instead:
`PlayModeStateChange.ExitingEditMode` fires synchronously *before* the
reload that follows an entry into Play Mode, so a marker written there and
consumed (deleted) by the very next `Load()` reliably identifies exactly
that one reload. The marker must be consumed exactly once per reload, by
the caller (`BridgeServer`), not independently by each store — an earlier
version of this fix had each store call
`ConsumeEnteringPlayModeMarker()` itself, so whichever ran first silently
ate the signal from the second. Full incident writeup in the package
README's `DECISIONS` heading.

Port binding: walks 17870→17879 on `HttpListenerException` (port taken),
writes the bound port atomically (`.tmp` + `File.Replace`/`Move`) to
`Library/UnityBridge/port` — clients read this file first, it's the
authoritative discovery mechanism (LOCKED).

**Permanent limitation, not a bug (confirmed 2026-07-18):** the bridge has
no existence independent of the Unity Editor process it runs inside —
there's no separate watchdog or supervisor. A genuine Editor crash and an
in-progress domain reload are indistinguishable from any HTTP client's
perspective; both present as a refused/reset connection, and the bridge has
no mechanism to report on its own death. The client-side reconnect contract
(30s ceiling) is the only defined boundary — past it, a caller should treat
the bridge as unavailable and hand off to a human rather than inferring
"still reloading" vs. "crashed." Diagnosing an actual crash requires reading
`Logs/Editor.log` and `%LOCALAPPDATA%\Temp\Unity\Editor\Crashes\` directly
on disk, entirely outside the bridge's own API surface.
