using UnityEditor;
using UnityEngine;

namespace UnityBridge.Editor
{
    // Shared GameObject-by-id resolution, reused verbatim (LOCKED, v2 task
    // brief "Object/component resolution") from the exact three-step chain
    // ObjectEndpoint already established in Phase 3: GlobalObjectId.TryParse
    // -> GlobalObjectIdentifierToEntityIdSlow -> EntityIdToObject. Every v2
    // mutation endpoint that takes an "id" body field goes through this
    // instead of re-deriving the chain.
    internal static class GameObjectResolver
    {
        static readonly string ZeroGuid = new string('0', 32);

        internal static GameObject ResolveOrThrow(string id)
        {
            if (string.IsNullOrEmpty(id) || !GlobalObjectId.TryParse(id, out GlobalObjectId gid))
                throw new BridgeHttpException(400, "invalid id format — expected a GlobalObjectId string from /scene/summary or /object/{id}");

            // Same EntityId-based pair ObjectEndpoint uses — Unity
            // 6000.5.2f1 hard-deprecates the InstanceID-based overloads
            // (error CS0619). Same resolve-or-null contract either way.
            var entityId = GlobalObjectId.GlobalObjectIdentifierToEntityIdSlow(gid);
            var go = EditorUtility.EntityIdToObject(entityId) as GameObject;
            if (go == null)
                throw new BridgeHttpException(404, "stale id — object gone or state reloaded; re-discover, don't retry");
            return go;
        }

        // Unity's documented signal for a non-persistent object (never
        // saved to a scene/prefab file) — see ObjectEndpoint's own copy of
        // this check for the full rationale (Phase 3 verification 3.6).
        internal static bool IsVolatile(GlobalObjectId gid) => gid.assetGUID.ToString() == ZeroGuid;
    }
}
