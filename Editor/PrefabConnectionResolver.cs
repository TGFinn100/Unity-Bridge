using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityBridge.Editor
{
    // v2.5: shared "is this GameObject a connected prefab instance" check,
    // reused by /act/prefab/apply and /act/prefab/revert (LOCKED: revert's
    // 400 not_prefab_instance is "same check as apply").
    internal static class PrefabConnectionResolver
    {
        internal static void RequireConnectedInstance(GameObject go, out string prefabGuid, out string prefabPath)
        {
            if (PrefabUtility.GetPrefabInstanceStatus(go) != PrefabInstanceStatus.Connected)
            {
                throw new MutationRejection(400, new Dictionary<string, object>
                {
                    { "tier", "act" }, { "error", "not_prefab_instance" }
                });
            }

            // Nearest corresponding source — for a Prefab Variant instance
            // this is the variant itself, not the ultimate base further up
            // the chain, matching ApplyPrefabInstance's own "applies to the
            // immediate source" behavior (empirically confirmed during
            // build per the brief's own deferral note).
            var source = PrefabUtility.GetCorrespondingObjectFromSource(go);
            prefabPath = source != null ? AssetDatabase.GetAssetPath(source) : null;
            prefabGuid = !string.IsNullOrEmpty(prefabPath) ? AssetDatabase.AssetPathToGUID(prefabPath) : null;
        }
    }
}
