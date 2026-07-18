# Index store

`Editor/Index/IndexStore.cs` + `Editor/Index/IndexRecords.cs`. In-memory
lists loaded from `Library/UnityBridge/*.jsonl`, queried via LINQ (LOCKED:
JSON-lines files, no SQLite — see the task brief's "Index store" section
for the full rationale).

## Thread safety

One `object _gate` lock guards every list (`_assets`, `_prefabComponents`,
`_sceneComponents`, `_scenePrefabInstances`, `_scriptTypes`). Indexed-tier
HTTP handlers read on background request threads; full scans and
incremental updates run on the main thread (`Tick()` /
`AssetPostprocessor` callbacks). Queries run start-to-finish inside the
lock rather than taking a snapshot-then-release — dataset scale (thousands
of rows for any real Unity project) makes holding the lock for one query
negligible.

## Readiness and staleness

- `IsReady` (`volatile bool _isReady`) — false until the first load/scan
  completes. Indexed-tier requests check this in `BridgeServer` and return
  503 `{"error":"indexing"}` rather than racing a 1s timeout against an
  index that isn't there yet.
- `Load()` — pure file I/O, safe to call from `BridgeServer`'s static
  constructor before any main-thread tick has run (no Unity API touched).
- `RunFullScanIfNeeded()` — called every `Tick()`; triggers a full rescan
  when `meta.json`'s `schemaVersion` doesn't match `IndexStore.SchemaVersion`
  or the persisted index is missing/corrupt (self-heal — acceptance
  criterion 5, verified 2.4).
- Incremental updates: `BridgeAssetPostprocessor` batches
  imported/deleted/moved assets, debounced (~250ms) via
  `BridgeAssetPostprocessor.FlushIfDue()` (also called every `Tick()`) —
  only the affected records get rescanned/rewritten, not a full rebuild.

## Schema (LOCKED — don't change without sign-off)

One JSON object per line, one file per record type, all under
`Library/UnityBridge/` (gitignored, regenerable):

- `assets.jsonl` — `AssetRecord`: `guid, path, type, name, sizeBytes, mtime`
- `prefab_components.jsonl` / `scene_components.jsonl` — both
  `ComponentRecord`: `guid, componentType, objectPath` (same shape, two
  different sources — `PrefabScanner` vs `SceneYamlParser`)
- `scene_prefab_instances.jsonl` — `ScenePrefabInstanceRecord`: `guid`
  (scene asset's own guid), `sourcePrefabGuid`, `objectPath` — the join key
  for "which scenes effectively contain component X via a prefab instance"
- `script_types.jsonl` — `ScriptTypeRecord`: `guid, className`
- `meta.json` — `IndexMeta`: `schemaVersion, lastFullScan`

Every record type has a matching `ToDict()`/`FromDict()` pair
(`IndexRecords.cs`) so the on-disk jsonl shape and HTTP response shape stay
identical — no separate serialization logic for "on disk" vs "over HTTP."

`JsonNum.ToLong(object)` (also in `IndexRecords.cs`) exists because
`MiniJson.Parse` hands back `int`/`long`/`double` depending on a number's
magnitude — any record field that's logically an integer needs this one
coercion path regardless of which numeric type came back.

## Scan scope

Both `RunFullScan()` and `ApplyIncrementalUpdate()` walk/accept paths from
across the whole `AssetDatabase` — `Assets/` and `Packages/` alike — with
one deliberate, matching exclusion in both: anything under
`IndexStore.SelfPackagePrefix` (`Packages/com.dalstar.unitybridge/`, the
bridge's own LOCKED package location) is never indexed. Rationale
(2026-07-18, found via a real bug): third-party `file:`/git-referenced
package source (Facepunch transport, NGO, etc.) is real project content and
must be queryable, so scanning can't be restricted to `Assets/` only — but
the bridge's own source must never be treated as just another queryable
asset, since an agent editing it could break the tool serving the query.

**The bug this fixes:** the two update paths used to disagree —
`RunFullScan` filtered to `Assets/` only, `ApplyIncrementalUpdate` had no
filter at all — so the index's actual scope silently depended on which
path happened to touch a given file first (a script only entered the index
if something re-triggered its `AssetPostprocessor` import *after* the
bridge was already running; anything whose only import happened during
initial bootstrap, before `_isReady` went true, was permanently invisible
to every subsequent full scan). Both paths must apply the exact same
exclusion rule, or this class of drift reappears.

## Gotchas already hit once

- **BOM**: jsonl files must be written with `new UTF8Encoding(false)`, not
  `File.WriteAllText(path, contents, Encoding.UTF8)` (that overload writes
  a BOM). Unity's own reader tolerates it, but it works against the
  "grep/diff-friendly" design goal.
- **Scene YAML override-ADDED components** get an approximated
  `objectPath` (attributed to the instance's own resolved path, not the
  exact nested position inside the prefab) — see the package README's
  `DECISIONS` for the full tradeoff and the documented escape hatch
  (fall back to native+join only) if this ever proves unreliable on a
  real project.
