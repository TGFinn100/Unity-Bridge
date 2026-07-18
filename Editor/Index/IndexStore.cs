using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnityBridge.Editor
{
    internal sealed class QueryFilter
    {
        public string Type;
        public string NameGlob;
        public string PathPrefix;
        public string HasComponent;
        public int Limit = 20;
    }

    // In-memory index over Library/UnityBridge/*.jsonl (LOCKED: JSON-lines,
    // in-memory queries, no SQLite). A single lock guards all list access —
    // indexed-tier HTTP handlers read on background threads, while full
    // scans and incremental updates run on the main thread. Dataset scale
    // (thousands of rows) makes holding the lock for a whole query
    // negligible, so queries run start-to-finish inside the lock rather than
    // risking a torn read on a released snapshot.
    internal static class IndexStore
    {
        internal const int SchemaVersion = 1;

        // The bridge's own package is never indexed, by either scan path
        // (LOCKED environment location, task brief line 59). Rationale
        // (2026-07-18): everything else under Packages/ — third-party
        // file:/git-referenced source (Facepunch transport, NGO, etc.) —
        // is real project content and should be queryable, so scanning
        // can't be limited to Assets/ only. But the bridge's own source
        // must stay out of its own index: nothing should treat it as just
        // another queryable/editable asset, since an edit there can break
        // the very tool serving the query.
        internal const string SelfPackagePrefix = "Packages/com.dalstar.unitybridge/";

        static readonly object _gate = new object();

        static List<AssetRecord> _assets = new List<AssetRecord>();
        static List<ComponentRecord> _prefabComponents = new List<ComponentRecord>();
        static List<ComponentRecord> _sceneComponents = new List<ComponentRecord>();
        static List<ScenePrefabInstanceRecord> _scenePrefabInstances = new List<ScenePrefabInstanceRecord>();
        static List<ScriptTypeRecord> _scriptTypes = new List<ScriptTypeRecord>();

        static volatile bool _isReady;
        static volatile bool _needsFullScan;
        static string _lastUpdatedIso = "";

        internal static bool IsReady => _isReady;
        internal static string LastUpdatedIso => _lastUpdatedIso;

        static string AssetsPath => Path.Combine(ProjectPaths.LibraryUnityBridgeDir, "assets.jsonl");
        static string PrefabComponentsPath => Path.Combine(ProjectPaths.LibraryUnityBridgeDir, "prefab_components.jsonl");
        static string SceneComponentsPath => Path.Combine(ProjectPaths.LibraryUnityBridgeDir, "scene_components.jsonl");
        static string ScenePrefabInstancesPath => Path.Combine(ProjectPaths.LibraryUnityBridgeDir, "scene_prefab_instances.jsonl");
        static string ScriptTypesPath => Path.Combine(ProjectPaths.LibraryUnityBridgeDir, "script_types.jsonl");
        static string MetaPath => Path.Combine(ProjectPaths.LibraryUnityBridgeDir, "meta.json");

        // Pure file I/O — safe to call from the BridgeServer static
        // constructor before any main-thread tick has run.
        internal static void Load()
        {
            try
            {
                if (!File.Exists(MetaPath))
                {
                    _needsFullScan = true;
                    return;
                }

                var meta = IndexMeta.FromDict((Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(MetaPath)));
                if (meta.SchemaVersion != SchemaVersion)
                {
                    _needsFullScan = true;
                    return;
                }

                _assets = JsonLinesIO.ReadLines(AssetsPath, AssetRecord.FromDict);
                _prefabComponents = JsonLinesIO.ReadLines(PrefabComponentsPath, ComponentRecord.FromDict);
                _sceneComponents = JsonLinesIO.ReadLines(SceneComponentsPath, ComponentRecord.FromDict);
                _scenePrefabInstances = JsonLinesIO.ReadLines(ScenePrefabInstancesPath, ScenePrefabInstanceRecord.FromDict);
                _scriptTypes = JsonLinesIO.ReadLines(ScriptTypesPath, ScriptTypeRecord.FromDict);

                _lastUpdatedIso = meta.LastFullScan;
                _isReady = true;
                _needsFullScan = false;
            }
            catch (Exception ex)
            {
                // Corrupt/partial jsonl on disk — treat exactly like a missing
                // index (self-heal, verification 2.4) rather than crashing.
                Debug.LogWarning($"[UnityBridge] Index load failed, will run a full scan: {ex.Message}");
                _needsFullScan = true;
            }
        }

        // Called every Tick() (main thread). No-op unless a scan is pending.
        internal static void RunFullScanIfNeeded()
        {
            if (!_needsFullScan) return;
            _needsFullScan = false;
            RunFullScan();
        }

        internal static void RunFullScan()
        {
            Debug.Log("[UnityBridge] Full index scan starting...");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var newAssets = new List<AssetRecord>();
            var newPrefabComponents = new List<ComponentRecord>();
            var newScriptTypes = new List<ScriptTypeRecord>();
            var scenePaths = new List<string>();

            foreach (string path in AssetDatabase.GetAllAssetPaths())
            {
                if (path.StartsWith(SelfPackagePrefix, StringComparison.Ordinal)) continue;
                string guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid)) continue;

                bool isDirectory = AssetDatabase.IsValidFolder(path);
                string type = AssetTypeClassifier.Classify(path, isDirectory);

                if (type == "prefab")
                {
                    var scan = PrefabScanner.Scan(path, guid);
                    newAssets.Add(scan.Asset);
                    newPrefabComponents.AddRange(scan.Components);
                    continue;
                }

                if (type == "scene")
                {
                    newAssets.Add(BuildGenericAssetRecord(path, guid, type, isDirectory));
                    scenePaths.Add(path);
                    continue;
                }

                newAssets.Add(BuildGenericAssetRecord(path, guid, type, isDirectory));

                if (type == "script")
                {
                    var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                    var cls = script != null ? script.GetClass() : null;
                    if (cls != null) newScriptTypes.Add(new ScriptTypeRecord { Guid = guid, ClassName = cls.Name });
                }
            }

            var scriptClassByGuid = newScriptTypes.ToDictionary(s => s.Guid, s => s.ClassName);
            var newSceneComponents = new List<ComponentRecord>();
            var newScenePrefabInstances = new List<ScenePrefabInstanceRecord>();

            foreach (string scenePath in scenePaths)
            {
                string sceneGuid = AssetDatabase.AssetPathToGUID(scenePath);
                ScanOneScene(scenePath, sceneGuid, scriptClassByGuid, newSceneComponents, newScenePrefabInstances);
            }

            string nowIso = DateTime.UtcNow.ToString("o");

            lock (_gate)
            {
                _assets = newAssets;
                _prefabComponents = newPrefabComponents;
                _sceneComponents = newSceneComponents;
                _scenePrefabInstances = newScenePrefabInstances;
                _scriptTypes = newScriptTypes;
                _lastUpdatedIso = nowIso;
            }

            WriteAllFiles(nowIso);
            _isReady = true;

            stopwatch.Stop();
            Debug.Log($"[UnityBridge] Full index scan complete in {stopwatch.ElapsedMilliseconds}ms: {newAssets.Count} assets, " +
                      $"{newPrefabComponents.Count} prefab components, {newSceneComponents.Count} scene components, " +
                      $"{newScenePrefabInstances.Count} scene prefab instances, {newScriptTypes.Count} script types.");
        }

        static void ScanOneScene(
            string scenePath, string sceneGuid,
            Dictionary<string, string> scriptClassByGuid,
            List<ComponentRecord> outComponents, List<ScenePrefabInstanceRecord> outInstances)
        {
            try
            {
                var result = SceneYamlParser.Parse(
                    ProjectPaths.ToAbsolute(scenePath),
                    sceneGuid,
                    guid => scriptClassByGuid.TryGetValue(guid, out var n) ? n : null,
                    ResolvePrefabRootName);
                outComponents.AddRange(result.Components);
                outInstances.AddRange(result.PrefabInstances);
            }
            catch (Exception ex)
            {
                // LOCKED: scenes that fail to parse are skipped with a
                // logged warning, never a crashed index.
                Debug.LogWarning($"[UnityBridge] Skipping scene \"{scenePath}\" — YAML parse failed: {ex.Message}");
            }
        }

        static string ResolvePrefabRootName(string prefabGuid)
        {
            string path = AssetDatabase.GUIDToAssetPath(prefabGuid);
            if (string.IsNullOrEmpty(path)) return null;
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            return go != null ? go.name : null;
        }

        static AssetRecord BuildGenericAssetRecord(string path, string guid, string type, bool isDirectory)
        {
            string absolute = ProjectPaths.ToAbsolute(path);
            long size = 0;
            string mtime = DateTime.UtcNow.ToString("o");

            if (isDirectory)
            {
                if (Directory.Exists(absolute)) mtime = Directory.GetLastWriteTimeUtc(absolute).ToString("o");
            }
            else if (File.Exists(absolute))
            {
                var info = new FileInfo(absolute);
                size = info.Length;
                mtime = info.LastWriteTimeUtc.ToString("o");
            }

            return new AssetRecord
            {
                Guid = guid,
                Path = path,
                Type = type,
                Name = isDirectory ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path),
                SizeBytes = size,
                Mtime = mtime
            };
        }

        // --- Incremental updates (debounced flush from BridgeAssetPostprocessor) ---

        internal static void ApplyIncrementalUpdate(HashSet<string> touchedPaths, HashSet<string> deletedPaths)
        {
            if (!_isReady) return; // an impending/ongoing full scan already covers current disk state

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var dirtyKinds = new HashSet<string>();

            lock (_gate)
            {
                foreach (string path in deletedPaths)
                    PurgeByPath(path, dirtyKinds);

                var scriptClassByGuid = _scriptTypes.ToDictionary(s => s.Guid, s => s.ClassName);

                foreach (string path in touchedPaths)
                {
                    // Must agree with RunFullScan's self-exclusion, or the
                    // index's content depends on accidental import timing
                    // (which path happened to touch a given file first)
                    // instead of a consistent rule.
                    if (path.StartsWith(SelfPackagePrefix, StringComparison.Ordinal)) continue;

                    // A path can legitimately appear in both sets within one
                    // batch (e.g. delete+recreate at the same path) — Unity
                    // reporting it as touched means it exists now, which
                    // wins over the deleted signal for that literal string.
                    // AssetPathToGUID naturally returns empty for anything
                    // that doesn't actually exist on disk, so this is
                    // self-correcting rather than needing a set-membership
                    // guard against deletedPaths.
                    string guid = AssetDatabase.AssetPathToGUID(path);
                    if (string.IsNullOrEmpty(guid)) continue; // gone again by the time we got here

                    PurgeByGuid(guid, dirtyKinds);

                    bool isDirectory = AssetDatabase.IsValidFolder(path);
                    string type = AssetTypeClassifier.Classify(path, isDirectory);

                    if (type == "prefab")
                    {
                        var scan = PrefabScanner.Scan(path, guid);
                        _assets.Add(scan.Asset);
                        _prefabComponents.AddRange(scan.Components);
                        dirtyKinds.Add("assets");
                        dirtyKinds.Add("prefabComponents");
                    }
                    else if (type == "scene")
                    {
                        _assets.Add(BuildGenericAssetRecord(path, guid, type, isDirectory));
                        dirtyKinds.Add("assets");
                        var comps = new List<ComponentRecord>();
                        var insts = new List<ScenePrefabInstanceRecord>();
                        ScanOneScene(path, guid, scriptClassByGuid, comps, insts);
                        _sceneComponents.AddRange(comps);
                        _scenePrefabInstances.AddRange(insts);
                        if (comps.Count > 0) dirtyKinds.Add("sceneComponents");
                        if (insts.Count > 0) dirtyKinds.Add("scenePrefabInstances");
                    }
                    else
                    {
                        _assets.Add(BuildGenericAssetRecord(path, guid, type, isDirectory));
                        dirtyKinds.Add("assets");

                        if (type == "script")
                        {
                            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                            var cls = script != null ? script.GetClass() : null;
                            if (cls != null)
                            {
                                _scriptTypes.Add(new ScriptTypeRecord { Guid = guid, ClassName = cls.Name });
                                dirtyKinds.Add("scriptTypes");
                            }
                        }
                    }
                }

                _lastUpdatedIso = DateTime.UtcNow.ToString("o");
            }

            if (dirtyKinds.Count > 0) WriteChangedFiles(dirtyKinds);

            stopwatch.Stop();
            Debug.Log($"[UnityBridge] Incremental update applied in {stopwatch.ElapsedMilliseconds}ms: " +
                      $"{touchedPaths.Count} touched, {deletedPaths.Count} deleted, dirty=[{string.Join(",", dirtyKinds)}].");
        }

        // Must be called with _gate held.
        static void PurgeByPath(string path, HashSet<string> dirtyKinds)
        {
            var matchedGuids = _assets.Where(a => a.Path == path).Select(a => a.Guid).ToList();
            if (matchedGuids.Count == 0) return;
            _assets.RemoveAll(a => a.Path == path);
            dirtyKinds.Add("assets");
            foreach (var guid in matchedGuids) PurgeByGuid(guid, dirtyKinds);
        }

        // Must be called with _gate held.
        static void PurgeByGuid(string guid, HashSet<string> dirtyKinds)
        {
            if (_assets.RemoveAll(a => a.Guid == guid) > 0) dirtyKinds.Add("assets");
            if (_prefabComponents.RemoveAll(c => c.Guid == guid) > 0) dirtyKinds.Add("prefabComponents");
            if (_sceneComponents.RemoveAll(c => c.Guid == guid) > 0) dirtyKinds.Add("sceneComponents");
            if (_scenePrefabInstances.RemoveAll(p => p.Guid == guid) > 0) dirtyKinds.Add("scenePrefabInstances");
            if (_scriptTypes.RemoveAll(s => s.Guid == guid) > 0) dirtyKinds.Add("scriptTypes");
        }

        static void WriteAllFiles(string nowIso)
        {
            List<AssetRecord> assets; List<ComponentRecord> prefabComponents, sceneComponents;
            List<ScenePrefabInstanceRecord> scenePrefabInstances; List<ScriptTypeRecord> scriptTypes;
            lock (_gate)
            {
                assets = _assets; prefabComponents = _prefabComponents; sceneComponents = _sceneComponents;
                scenePrefabInstances = _scenePrefabInstances; scriptTypes = _scriptTypes;
            }

            JsonLinesIO.WriteLines(AssetsPath, assets, a => a.ToDict());
            JsonLinesIO.WriteLines(PrefabComponentsPath, prefabComponents, c => c.ToDict());
            JsonLinesIO.WriteLines(SceneComponentsPath, sceneComponents, c => c.ToDict());
            JsonLinesIO.WriteLines(ScenePrefabInstancesPath, scenePrefabInstances, p => p.ToDict());
            JsonLinesIO.WriteLines(ScriptTypesPath, scriptTypes, s => s.ToDict());
            WriteMeta(new IndexMeta { SchemaVersion = SchemaVersion, LastFullScan = nowIso });
        }

        static void WriteChangedFiles(HashSet<string> dirtyKinds)
        {
            List<AssetRecord> assets = null; List<ComponentRecord> prefabComponents = null, sceneComponents = null;
            List<ScenePrefabInstanceRecord> scenePrefabInstances = null; List<ScriptTypeRecord> scriptTypes = null;
            lock (_gate)
            {
                if (dirtyKinds.Contains("assets")) assets = new List<AssetRecord>(_assets);
                if (dirtyKinds.Contains("prefabComponents")) prefabComponents = new List<ComponentRecord>(_prefabComponents);
                if (dirtyKinds.Contains("sceneComponents")) sceneComponents = new List<ComponentRecord>(_sceneComponents);
                if (dirtyKinds.Contains("scenePrefabInstances")) scenePrefabInstances = new List<ScenePrefabInstanceRecord>(_scenePrefabInstances);
                if (dirtyKinds.Contains("scriptTypes")) scriptTypes = new List<ScriptTypeRecord>(_scriptTypes);
            }

            if (assets != null) JsonLinesIO.WriteLines(AssetsPath, assets, a => a.ToDict());
            if (prefabComponents != null) JsonLinesIO.WriteLines(PrefabComponentsPath, prefabComponents, c => c.ToDict());
            if (sceneComponents != null) JsonLinesIO.WriteLines(SceneComponentsPath, sceneComponents, c => c.ToDict());
            if (scenePrefabInstances != null) JsonLinesIO.WriteLines(ScenePrefabInstancesPath, scenePrefabInstances, p => p.ToDict());
            if (scriptTypes != null) JsonLinesIO.WriteLines(ScriptTypesPath, scriptTypes, s => s.ToDict());
            // meta.json's lastFullScan intentionally untouched here — it
            // tracks full scans only, per the LOCKED schema field name.
        }

        static void WriteMeta(IndexMeta meta)
        {
            string dir = ProjectPaths.LibraryUnityBridgeDir;
            Directory.CreateDirectory(dir);
            string tempPath = MetaPath + ".tmp";
            File.WriteAllText(tempPath, MiniJson.Write(meta.ToDict()));
            if (File.Exists(MetaPath)) File.Replace(tempPath, MetaPath, null);
            else File.Move(tempPath, MetaPath);
        }

        // --- Queries (indexed tier — answered directly on the request thread) ---

        internal static (List<AssetRecord> items, int total) Query(QueryFilter filter)
        {
            lock (_gate)
            {
                IEnumerable<AssetRecord> candidates = _assets;

                if (!string.IsNullOrEmpty(filter.Type))
                    candidates = candidates.Where(a => string.Equals(a.Type, filter.Type, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(filter.PathPrefix))
                    candidates = candidates.Where(a => a.Path.StartsWith(filter.PathPrefix, StringComparison.Ordinal));

                if (!string.IsNullOrEmpty(filter.NameGlob))
                {
                    var regex = GlobToRegex(filter.NameGlob);
                    candidates = candidates.Where(a => regex.IsMatch(a.Name));
                }

                if (!string.IsNullOrEmpty(filter.HasComponent))
                {
                    var allowedGuids = GuidsWithComponent(filter.HasComponent);
                    candidates = candidates.Where(a => allowedGuids.Contains(a.Guid));
                }

                var sorted = candidates.OrderBy(a => a.Path, StringComparer.Ordinal).ToList();
                int total = sorted.Count;
                int limit = Math.Max(1, Math.Min(filter.Limit, 100));
                var page = sorted.Take(limit).ToList();
                return (page, total);
            }
        }

        // Must be called with _gate held.
        static HashSet<string> GuidsWithComponent(string componentType)
        {
            var matchingPrefabGuids = new HashSet<string>(
                _prefabComponents.Where(c => string.Equals(c.ComponentType, componentType, StringComparison.Ordinal))
                                  .Select(c => c.Guid));

            var result = new HashSet<string>(
                _sceneComponents.Where(c => string.Equals(c.ComponentType, componentType, StringComparison.Ordinal))
                                 .Select(c => c.Guid));
            result.UnionWith(matchingPrefabGuids);

            foreach (var pi in _scenePrefabInstances)
                if (matchingPrefabGuids.Contains(pi.SourcePrefabGuid))
                    result.Add(pi.Guid);

            return result;
        }

        static Regex GlobToRegex(string glob)
        {
            string pattern = "^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            return new Regex(pattern, RegexOptions.IgnoreCase);
        }

        internal static AssetRecord GetAsset(string guid)
        {
            lock (_gate) { return _assets.FirstOrDefault(a => a.Guid == guid); }
        }

        internal static List<ComponentRecord> GetPrefabComponents(string guid)
        {
            lock (_gate) { return _prefabComponents.Where(c => c.Guid == guid).ToList(); }
        }

        internal static List<ComponentRecord> GetSceneComponents(string guid)
        {
            lock (_gate) { return _sceneComponents.Where(c => c.Guid == guid).ToList(); }
        }

        internal static List<ScenePrefabInstanceRecord> GetScenePrefabInstances(string guid)
        {
            lock (_gate) { return _scenePrefabInstances.Where(p => p.Guid == guid).ToList(); }
        }
    }
}
