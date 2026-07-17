# Response envelope conventions

All bodies are `Dictionary<string, object>`, serialized via `MiniJson.Write`
(`Editor/Json/MiniJson.cs`) — compact, no pretty-printing (LOCKED, the
consumer is a model).

## Fields every successful response carries

- `"tier"`: `"meta" | "indexed" | "live"` — always present.
- `"indexedAt"`: ISO timestamp, indexed-tier responses only
  (`IndexStore.LastUpdatedIso`).
- `"frame"`: `Time.frameCount`, added by `BridgeState.AddFrameIfPlaying(body)`
  to any live-tier response when `EditorApplication.isPlaying` — the
  brief's "live answers are frame-stale by definition in play mode"
  disclosure. Every live handler calls this after building its body.

## The truncated/total/hint trio (LOCKED)

`ResponseCapping.ApplyListCap(body, listKey, total, hint)`
(`Editor/ResponseCapping.cs`) is the shared implementation: while the whole
body's JSON exceeds 16384 bytes, `RemoveAt(list.Count - 1)` on
`body[listKey]` (structural truncation — whole items dropped, never
byte-level, so the response always parses). Then sets `body["truncated"]`,
`body["total"]` (the true match count *before* capping), and
`body["hint"]` (only when truncated) naming the narrowing move.

**This helper only handles one flat top-level list.** Two precedents for
when a response's shape doesn't fit that:

- `AssetEndpoint`'s scene branch has two candidate lists (`components` and
  `prefabInstances`). `ApplyListCap` shrinks `components`; a manual
  best-effort loop (`while (... > 16384) instanceList.RemoveAt(...)`)
  handles `prefabInstances` without its own truncation metadata (the
  LOCKED trio is meaningless split across two lists in one response).
- `ObjectEndpoint`'s object tree is nested (`children` inside `children`,
  up to depth 2), not flat. It writes its own recursive trim
  (`FindWidestChildrenList` + `TrimToCap`): repeatedly drop the last child
  of whichever node — anywhere in the tree — currently has the most direct
  children, until the whole body fits, then set `truncated`/`total`/`hint`
  manually at the top level. Same structural-truncation contract, just
  walked recursively instead of over one list.

If you're adding an endpoint with a genuinely new response shape (not one
flat list, not a small fixed tree), check whether either precedent above
fits before inventing a third pattern.

## Error shapes, by cause

- Handler throws `BridgeHttpException(code, message)` → `{"error": message}`
  at that status code. No `tier` key added automatically — callers that
  want `tier` in an error body add it themselves (see 503/504 below).
- Indexed-tier request while `!IndexStore.IsReady` → 503
  `{"tier":"indexed","error":"indexing","readyState":...}`.
- Live/queued-tier request that hits `endpoint.TimeoutMs` → 504
  `{"tier":endpoint.Tier,"error":"timeout","readyState":...,"elapsedMs":...}`.
- Uncaught exception in a handler → 500 `{"error": ex.Message}`.
- No route matches → 404 `{"error":"unknown endpoint — see /help"}`.
- Non-localhost `Host` header → 403 `{"error":"Host header must be localhost or 127.0.0.1"}`.
- Malformed POST JSON body → 400 `{"error":"malformed JSON body"}`.

## `MiniJson.Write`'s type-conversion gotcha

`WriteValue`'s switch handles `null`, `bool`, `string`, `int`, `long`,
`float`, `double`, `IDictionary<string,object>`, `IEnumerable<object>` —
anything else falls through to `value.ToString()`, which is almost never
what you want for a Unity type (`Vector3.ToString()` → `"(1.0, 2.0, 3.0)"`,
not JSON; an enum's `.ToString()` happens to work but a `Color` or `Rect`
won't). **Always convert Unity structs to a `Dictionary<string,object>` (or
a primitive) yourself before handing a value to a response body** — see
`SerializedValueExtractor` in `shared-helpers.md` for the reference
implementation of this conversion for serialized component fields.
