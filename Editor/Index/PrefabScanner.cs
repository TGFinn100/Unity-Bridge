using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityBridge.Editor
{
    // Builds an AssetRecord + prefab_components rows for one prefab. Prefabs
    // (unlike scenes) are cheap to load individually via AssetDatabase, so
    // this uses the live GameObject/Component API directly instead of a text
    // parse — Component.GetType().Name gives the real class name for both
    // built-in components and MonoBehaviour subclasses with no extra join.
    internal static class PrefabScanner
    {
        internal struct ScanResult
        {
            public AssetRecord Asset;
            public List<ComponentRecord> Components;
        }

        internal static ScanResult Scan(string assetPath, string guid)
        {
            var components = new List<ComponentRecord>();

            var asset = new AssetRecord
            {
                Guid = guid,
                Path = assetPath,
                Type = "prefab",
                Name = Path.GetFileNameWithoutExtension(assetPath),
                SizeBytes = GetFileSize(assetPath),
                Mtime = GetMtimeIso(assetPath)
            };

            var go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (go != null)
            {
                CollectComponents(go.transform, go.name, guid, components);
            }

            return new ScanResult { Asset = asset, Components = components };
        }

        static void CollectComponents(Transform t, string objectPath, string guid, List<ComponentRecord> outList)
        {
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null) continue; // missing script reference — nothing to name
                outList.Add(new ComponentRecord { Guid = guid, ComponentType = c.GetType().Name, ObjectPath = objectPath });
            }

            for (int i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i);
                CollectComponents(child, objectPath + "/" + child.name, guid, outList);
            }
        }

        static long GetFileSize(string assetPath)
        {
            string full = ProjectPaths.ToAbsolute(assetPath);
            return File.Exists(full) ? new FileInfo(full).Length : 0;
        }

        static string GetMtimeIso(string assetPath)
        {
            string full = ProjectPaths.ToAbsolute(assetPath);
            DateTime t = File.Exists(full) ? File.GetLastWriteTimeUtc(full) : DateTime.UtcNow;
            return t.ToString("o");
        }
    }
}
