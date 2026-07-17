# /help and /help/{topic} generation

`Editor/Endpoints/HelpEndpoint.cs` registers itself the same way as any
other endpoint (self-registration, no special-casing in `BridgeServer`).
Docs are generated from the same `EndpointInfo` records every handler
registers — there is no separate authoring step, which is what keeps
`/help` from drifting out of sync with the actual implementation (LOCKED
design goal).

## `GET /help`

`HandleIndex` iterates `EndpointRegistry.All` and projects
`{method, path, summary}` per entry. This is why `Summary` must stay
≤8 words — every registered endpoint's summary sits in this one response,
and the whole thing is budgeted at <600 tokens (verified in
human-verification 1.3).

## `GET /help/{topic}`

`HandleTopic` looks up `EndpointRegistry.FindByTopic(ctx.Topic)` (matched on
`TopicKey`, not `Path` — a separate lookup from route resolution) and
returns the full doc: `method, path, endpointTier, summary, params,
exampleRequest, exampleResponse`. Unknown topic → 404 listing all valid
topics (`EndpointRegistry.AllTopics()`).

## What a new endpoint must supply for this to work

Every field on `EndpointInfo` that `/help`/`/help/{topic}` reads:

- `Method`, `Path` — display path; for a topic route this is
  display-only (e.g. `/object/{id}`), the actual routing prefix is
  `ParamPrefix`.
- `TopicKey` — unique across all endpoints. Used for `/help/{topic}`
  lookup. Note: `IsTopicRoute` is purely a *routing* concern (does this
  endpoint's own path need prefix-matching) and doesn't exclude it from
  being a valid help topic — `/help/{topic}` and `/asset/{guid}` are
  themselves valid topics too (e.g. `GET /help/asset`).
- `Tier` — shown as `endpointTier` in the full doc.
- `Summary` — ≤8 words, shown in the index.
- `Params` — `string[]`, human-readable one-liners (see any existing
  endpoint for the format: `"name (type, required/optional): description"`).
- `ExampleRequest` / `ExampleResponseAbbrev` — one each, shown verbatim in
  the full doc.

No separate registration step is needed for `/help` itself — adding an
`EndpointInfo` to `EndpointRegistry` via `Register()` is sufficient; both
`/help` and `/help/{topic}` pick it up automatically by iterating
`EndpointRegistry.All`/`FindByTopic`.
