# Adding a new endpoint — checklist

Generalized from the Phase 3 build (four endpoints, one shared plumbing
change). Follow in order; each step links to the file with the fuller
explanation if you need it.

## 1. Decide the tier

- **`meta`** — only reads pre-cached, thread-safe state (`BridgeState`).
  Never calls the Unity API. Rare — `/ping`/`/help` are the only examples.
- **`indexed`** — only reads `IndexStore`. Answered directly on the request
  thread, 1000ms budget, 503 if the index isn't ready yet.
- **`live`** — touches the Unity API (scene, GameObjects, EditorApplication,
  anything not pre-cached). Routes through the main-thread queue
  automatically — this path already exists, just set `Tier = "live"`.

See `routing-and-tiers.md` for why each tier dispatches the way it does.

## 2. Create `Editor/Endpoints/<Name>Endpoint.cs`

A static `Register()` building one `EndpointInfo` and calling
`EndpointRegistry.Add(...)`, plus a static `Handle(BridgeRequestContext ctx)`.
Required `EndpointInfo` fields, and why `/help` needs each one, are in
`help-generation.md`. If the endpoint takes a path parameter (like
`/asset/{guid}` or `/object/{id}`), set `IsTopicRoute = true` and
`ParamPrefix = "/yourpath/"` — see `routing-and-tiers.md`'s router section
for exactly how prefix matching captures the remainder.

## 3. Read input from `ctx`

- `ctx.Topic` — path param, if any.
- `ctx.Body` — parsed POST JSON, null if none/GET.
- `ctx.Query` — parsed GET query string (added Phase 3 for `/object/{id}`'s
  `depth`/`components` and `/logs/tail`'s `n`/`severity`) — never null,
  empty dict if the request had no query string.

Validate explicitly; throw `BridgeHttpException(400, "...")` for bad input
rather than silently defaulting to something that produces a confusing
response. Reserve a stable 404 hint (`"stale id — ...; re-discover, don't
retry"` is the existing convention for a not-found-by-id case) rather than
inventing new wording per endpoint.

## 4. Build the response body

- Always include `"tier"`.
- `"indexedAt"` if indexed-tier.
- Call `BridgeState.AddFrameIfPlaying(body)` if live-tier.
- Cap size: `ResponseCapping.ApplyListCap` for one flat list; if your shape
  doesn't fit that (multiple lists, a nested tree), see the two existing
  precedents in `response-envelope.md` before writing a third pattern from
  scratch.

Full envelope/error-shape reference: `response-envelope.md`.

## 5. Register it

Add the new `Register()` call to `BridgeServer.RegisterEndpoints()` — the
only place a new endpoint needs manual wiring beyond its own file.
`/help`/`/help/{topic}` pick it up automatically; no separate step needed
there.

## 6. Compile

Cannot be triggered programmatically — ask the user to focus the Unity
Editor window. Then check `Logs/Editor.log` for `error CS` / the last
`Tundra build` result rather than assuming success (see the
`unity-editor-log-tailing` skill if compile status is ambiguous).

## 7. Verify against the human-verification doc, then commit

This repo's commit policy (see the sandbox project's `TODO.md` "Working
Rules") is strict: walk the endpoint's corresponding
`unity-bridge-human-verification.md` step(s) with the user driving the
Editor side, and get explicit sign-off before committing — "it compiles"
is not the same as "it works." No partial passes get committed either.

## 8. Log anything non-obvious in `DECISIONS`

Any ambiguous-but-reasonable v1 interpretation, any Unity-version-forced
API change (like Phase 3's `InstanceID`→`EntityId` swap), any deviation
from a LOCKED brief detail — goes in the package README's `DECISIONS`
heading, never applied silently. If nothing unusual came up, that's fine
too; not every endpoint needs a `DECISIONS` entry.
