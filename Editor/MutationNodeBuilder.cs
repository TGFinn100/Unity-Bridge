using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityBridge.Editor
{
    // v2: the single-node response shape shared by every GameObject-lifecycle
    // endpoint's "object" field (create/duplicate/reparent/rename) — same
    // node shape as GET /object/{id}?components=values, just never nested
    // (no "children" — no v2 endpoint takes a depth param).
    internal static class MutationNodeBuilder
    {
        internal static Dictionary<string, object> BuildNode(GameObject go)
        {
            var gid = GlobalObjectId.GetGlobalObjectIdSlow(go);
            var node = new Dictionary<string, object>
            {
                { "id", gid.ToString() },
                { "name", go.name },
                { "active", go.activeSelf },
                { "componentNames", ComponentSerializer.ComponentNames(go) },
                { "components", ComponentSerializer.ComponentValues(go) }
            };
            if (GameObjectResolver.IsVolatile(gid)) node["volatile"] = true;
            return node;
        }
    }
}
