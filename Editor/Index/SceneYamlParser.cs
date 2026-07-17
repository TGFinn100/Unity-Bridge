using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UnityBridge.Editor
{
    // Flat text parse of a .unity scene file (LOCKED design — task brief's
    // "Index store" section: scenes are read as text, never loaded through
    // the Unity API, because opening every scene in a project on the main
    // thread is too expensive for an incremental indexer).
    //
    // Each serialized object is a "--- !u!<classID> &<fileID>" header
    // followed immediately by a line whose first token IS the type name
    // (Transform:, BoxCollider:, MonoBehaviour:, PrefabInstance:, ...) — no
    // classID lookup table needed. Components back-reference their owning
    // GameObject via m_GameObject: {fileID: X}; hierarchy is rebuilt by
    // walking Transform docs' m_Father links up to a root (fileID 0).
    //
    // Bounded semantics (LOCKED, documented in /help/query and the skill
    // file): override-REMOVED components on prefab instances are not
    // resolved (a removed component can still appear in results — rare,
    // benign false positive for a read-only tool). Override-ADDED components
    // (ordinary component blocks whose m_GameObject points at a "stripped"
    // GameObject proxy owned by a PrefabInstance) are captured, but their
    // objectPath is approximated as the owning instance's own resolved path
    // rather than the exact nested position inside the prefab — resolving
    // that precisely would require parsing the source prefab's own hierarchy
    // and matching m_CorrespondingSourceObject fileIDs against it, which is
    // exactly the kind of complexity the task brief's escape hatch exists
    // for (fall back to native+join only, drop added-component detection,
    // flag as a deviation) if this approximation proves troublesome in
    // practice.
    internal static class SceneYamlParser
    {
        static readonly Regex HeaderRegex = new Regex(@"^--- !u!\d+ &(-?\d+)(\s+stripped)?\s*$");

        internal struct ScanResult
        {
            public List<ComponentRecord> Components;
            public List<ScenePrefabInstanceRecord> PrefabInstances;
        }

        sealed class YamlDoc
        {
            public string FileId;
            public bool Stripped;
            public string TypeName;
            public List<string> Lines;
        }

        internal static ScanResult Parse(
            string absolutePath,
            string sceneGuid,
            Func<string, string> resolveScriptClassName,
            Func<string, string> resolvePrefabRootName)
        {
            var result = new ScanResult
            {
                Components = new List<ComponentRecord>(),
                PrefabInstances = new List<ScenePrefabInstanceRecord>()
            };

            var docs = SplitDocuments(File.ReadAllLines(absolutePath));
            if (docs.Count == 0) return result;

            var byId = new Dictionary<string, YamlDoc>();
            foreach (var d in docs) byId[d.FileId] = d;

            // GameObject display names (native objects only — stripped proxy
            // docs carry no m_Name).
            var goName = new Dictionary<string, string>();
            foreach (var d in docs)
            {
                if (d.TypeName != "GameObject" || d.Stripped) continue;
                string name = ExtractScalar(d.Lines, "m_Name");
                goName[d.FileId] = string.IsNullOrEmpty(name) ? "GameObject_" + d.FileId : name;
            }

            // GameObject -> parent GameObject, derived via each GameObject's
            // own Transform's m_Father link.
            var goParent = new Dictionary<string, string>();
            foreach (var d in docs)
            {
                if (d.TypeName != "Transform" && d.TypeName != "RectTransform") continue;
                string goId = ExtractInlineFileId(d.Lines, "m_GameObject");
                if (goId == null) continue;

                string fatherTransformId = ExtractInlineFileId(d.Lines, "m_Father");
                string fatherGoId = null;
                if (fatherTransformId != null && fatherTransformId != "0" &&
                    byId.TryGetValue(fatherTransformId, out var fatherDoc))
                {
                    fatherGoId = ExtractInlineFileId(fatherDoc.Lines, "m_GameObject");
                }
                goParent[goId] = fatherGoId;
            }

            var pathCache = new Dictionary<string, string>();
            string ResolveGoPath(string goId, HashSet<string> visiting)
            {
                if (goId == null) return null;
                if (pathCache.TryGetValue(goId, out var cached)) return cached;
                if (!visiting.Add(goId)) return goName.TryGetValue(goId, out var n) ? n : goId; // cycle guard

                string name = goName.TryGetValue(goId, out var nm) ? nm : "GameObject_" + goId;
                string path = name;
                if (goParent.TryGetValue(goId, out var parentGoId) && parentGoId != null)
                {
                    string parentPath = ResolveGoPath(parentGoId, visiting);
                    if (!string.IsNullOrEmpty(parentPath)) path = parentPath + "/" + name;
                }
                pathCache[goId] = path;
                return path;
            }

            // Stripped GameObject proxies -> owning PrefabInstance fileID.
            var strippedGoOwner = new Dictionary<string, string>();
            foreach (var d in docs)
            {
                if (d.TypeName != "GameObject" || !d.Stripped) continue;
                string prefabInstanceId = ExtractInlineFileId(d.Lines, "m_PrefabInstance");
                if (prefabInstanceId != null) strippedGoOwner[d.FileId] = prefabInstanceId;
            }

            // PrefabInstance docs -> scene_prefab_instances rows + resolved
            // path, keyed for the override-added-component pass below.
            var prefabInstancePath = new Dictionary<string, string>();
            foreach (var d in docs)
            {
                if (d.TypeName != "PrefabInstance") continue;

                var sourceMapping = ExtractInlineMapping(d.Lines, "m_SourcePrefab");
                if (sourceMapping == null || !sourceMapping.TryGetValue("guid", out var sourcePrefabGuid) ||
                    string.IsNullOrEmpty(sourcePrefabGuid))
                {
                    continue; // can't join without the source guid
                }

                string parentTransformId = ExtractInlineFileId(d.Lines, "m_TransformParent");
                string parentGoId = null;
                if (parentTransformId != null && parentTransformId != "0" &&
                    byId.TryGetValue(parentTransformId, out var parentTransformDoc))
                {
                    parentGoId = ExtractInlineFileId(parentTransformDoc.Lines, "m_GameObject");
                }
                string parentPath = parentGoId != null ? ResolveGoPath(parentGoId, new HashSet<string>()) : null;

                string instanceName = resolvePrefabRootName?.Invoke(sourcePrefabGuid);
                if (string.IsNullOrEmpty(instanceName)) instanceName = "PrefabInstance_" + d.FileId;
                string objectPath = string.IsNullOrEmpty(parentPath) ? instanceName : parentPath + "/" + instanceName;

                prefabInstancePath[d.FileId] = objectPath;
                result.PrefabInstances.Add(new ScenePrefabInstanceRecord
                {
                    Guid = sceneGuid,
                    SourcePrefabGuid = sourcePrefabGuid,
                    ObjectPath = objectPath
                });
            }

            // Every remaining doc that isn't a GameObject/PrefabInstance is a
            // component of some kind — native or override-added.
            foreach (var d in docs)
            {
                if (d.TypeName == "GameObject" || d.TypeName == "PrefabInstance") continue;

                string goId = ExtractInlineFileId(d.Lines, "m_GameObject");
                if (goId == null) continue;

                string objectPath;
                if (goName.ContainsKey(goId))
                {
                    objectPath = ResolveGoPath(goId, new HashSet<string>());
                }
                else if (strippedGoOwner.TryGetValue(goId, out var ownerInstanceId) &&
                         prefabInstancePath.TryGetValue(ownerInstanceId, out var instPath))
                {
                    objectPath = instPath; // override-added — approximated to the instance root, see class doc
                }
                else
                {
                    continue; // unresolvable owner
                }

                string componentType = d.TypeName;
                if (componentType == "MonoBehaviour")
                {
                    var scriptMapping = ExtractInlineMapping(d.Lines, "m_Script");
                    string scriptGuid = scriptMapping != null && scriptMapping.TryGetValue("guid", out var sg) ? sg : null;
                    string className = scriptGuid != null ? resolveScriptClassName?.Invoke(scriptGuid) : null;
                    if (string.IsNullOrEmpty(className)) continue; // unresolvable script reference — can't name it
                    componentType = className;
                }

                result.Components.Add(new ComponentRecord { Guid = sceneGuid, ComponentType = componentType, ObjectPath = objectPath });
            }

            return result;
        }

        static List<YamlDoc> SplitDocuments(string[] lines)
        {
            var docs = new List<YamlDoc>();
            YamlDoc current = null;
            List<string> body = null;

            for (int i = 0; i < lines.Length; i++)
            {
                var m = HeaderRegex.Match(lines[i]);
                if (m.Success)
                {
                    if (current != null) { current.Lines = body; docs.Add(current); }

                    current = new YamlDoc { FileId = m.Groups[1].Value, Stripped = m.Groups[2].Success };
                    body = new List<string>();

                    if (i + 1 < lines.Length)
                    {
                        string typeLine = lines[i + 1].TrimEnd();
                        int colon = typeLine.IndexOf(':');
                        current.TypeName = (colon >= 0 ? typeLine.Substring(0, colon) : typeLine).Trim();
                        i++; // consumed the type line
                    }
                    continue;
                }
                if (current != null) body.Add(lines[i]);
            }
            if (current != null) { current.Lines = body; docs.Add(current); }
            return docs;
        }

        static string ExtractScalar(List<string> lines, string key)
        {
            string prefix = key + ":";
            foreach (var line in lines)
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith(prefix)) return trimmed.Substring(prefix.Length).Trim();
            }
            return null;
        }

        static string ExtractInlineFileId(List<string> lines, string key)
        {
            var mapping = ExtractInlineMapping(lines, key);
            return mapping != null && mapping.TryGetValue("fileID", out var id) ? id : null;
        }

        static Dictionary<string, string> ExtractInlineMapping(List<string> lines, string key)
        {
            string prefix = key + ":";
            foreach (var line in lines)
            {
                string trimmed = line.TrimStart();
                if (!trimmed.StartsWith(prefix)) continue;

                int braceStart = trimmed.IndexOf('{', prefix.Length);
                if (braceStart < 0) return null;
                int braceEnd = trimmed.IndexOf('}', braceStart + 1);
                if (braceEnd < 0) return null;

                var result = new Dictionary<string, string>();
                foreach (var part in trimmed.Substring(braceStart + 1, braceEnd - braceStart - 1).Split(','))
                {
                    int c = part.IndexOf(':');
                    if (c < 0) continue;
                    result[part.Substring(0, c).Trim()] = part.Substring(c + 1).Trim();
                }
                return result;
            }
            return null;
        }
    }
}
