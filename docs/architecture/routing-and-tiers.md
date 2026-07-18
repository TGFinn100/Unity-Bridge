# Routing and tiers

Two files own this: `Editor/EndpointRegistry.cs` (the data model + router)
and `Editor/BridgeServer.cs` (the listener, dispatch, and domain-reload
lifecycle). There is no separate "dispatcher" class beyond these two.

## The data model (`EndpointRegistry.cs`)

- `EndpointInfo` — one per endpoint: `Method`, `Path` (display only for
  topic routes), `TopicKey`, `Tier` (`"meta"|"indexed"|"live"`), `Summary`
  (≤8 words), `Params`, `ExampleRequest`, `ExampleResponseAbbrev`,
  `TimeoutMs`, `IsTopicRoute`/`ParamPrefix`, `Handler`.
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

## The three tiers

**`meta`** (`/ping`, `/help`, `/help/{topic}`) — answered directly on the
background request thread, bypassing the main-thread queue entirely.
Deliberate: Unity can run a "forced synchronous recompile" that blocks
`EditorApplication.update` (and therefore `Tick()`) for the whole
compile+reload window. If meta requests went through the queue, `/ping`
could never report `readyState:"compiling"` during exactly the window
that matters. Meta handlers may only read pre-cached, thread-safe state
(`BridgeState`'s `volatile` fields) — never call into the Unity API
directly. `TimeoutMs` in practice: 5000.

**`indexed`** (`/query`, `/asset/{guid}`) — also answered directly on the
request thread (LOCKED per the task brief: "no main thread involved — slow
means broken, fail fast"), gated on `IndexStore.IsReady` (503
`{"tier":"indexed","error":"indexing","readyState":...}` if not). Thread
safety comes from `IndexStore`'s own internal lock, not from this dispatch.
`TimeoutMs` in practice: 1000 (fail-fast, per the LOCKED rationale above).

**`live`** (`/scene/summary`, `/object/{id}`, `/logs/tail`, `/playmode`) —
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

## Domain reload survival

`[InitializeOnLoad]` on `BridgeServer` re-runs its whole static constructor
after every domain reload — all static state (the queue, cached endpoint
list, etc.) resets for free. The constructor also subscribes:
`CompilationPipeline.compilationStarted` → `BridgeState.MarkCompiling()`,
`AssemblyReloadEvents.beforeAssemblyReload` → `BridgeState.MarkReloading()`
+ stop the listener (clean socket teardown — this is what makes GATE 1's
"no address-in-use" requirement hold), `EditorApplication.quitting` → stop
listener, `EditorApplication.playModeStateChanged` →
`PlaymodeEndpoint.OnPlayModeStateChanged` (Phase 3, entry-time marker file),
`Application.logMessageReceivedThreaded` → `LogBuffer.OnLogMessage` (Phase
3, console ring buffer). None of these need explicit unsubscription — a
domain reload discards the old assembly (and every delegate pointing into
it) wholesale.

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
