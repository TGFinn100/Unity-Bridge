using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityBridge.Editor
{
    // POST /act/prefab/instantiate (v2.5 LOCKED). Synchronous mutation, same
    // dispatch model v2 established (see routing-and-tiers.md).
    internal static class PrefabInstantiateEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "POST",
                Path = "/act/prefab/instantiate",
                TopicKey = "act-prefab-instantiate",
                Tier = "act",
                Synchronous = true,
                Summary = "Instantiate a prefab asset into the scene",
                Params = new[]
                {
                    "prefabGuid (string, required): GUID of the prefab asset to instantiate, from /query or /asset/{guid}",
                    "parent (string, optional): GlobalObjectId to parent under; default/null = scene root",
                    "name (string, optional): default = the prefab asset's own name",
                    "position (object {x,y,z}, optional): local position, default zero",
                    "rotation (object {x,y,z,w}, optional): local rotation as a Quaternion, default identity {0,0,0,1}",
                    "scale (object {x,y,z}, optional): local scale, default one"
                },
                ExampleRequest = "POST /act/prefab/instantiate {\"prefabGuid\":\"...\"} (header X-Bridge-Token: <token>)",
                ExampleResponseAbbrev = "{\"tier\":\"act\",\"autoSaved\":true,\"object\":{\"id\":\"...\",\"name\":\"Cube\",\"active\":true,\"componentNames\":[\"Transform\"],\"components\":[{\"type\":\"Transform\",\"fields\":{...}}]}}",
                TimeoutMs = 5000,
                BuildMutation = Build
            });
        }

        static BridgeHandlerResult Build(Dictionary<string, object> body)
        {
            body = body ?? new Dictionary<string, object>();

            string prefabGuid = MutationBody.GetString(body, "prefabGuid");
            if (string.IsNullOrEmpty(prefabGuid))
                throw new MutationRejection(400, new Dictionary<string, object> { { "tier", "act" }, { "error", "missing_prefab_guid" } });

            GameObject prefabAsset = ResolvePrefabAssetOrThrow(prefabGuid);

            // Same "omitted or explicit null both mean scene root" default
            // as /act/gameobject/create (LOCKED, reused verbatim here).
            Transform parent = null;
            if (body.TryGetValue("parent", out var rawParent) && rawParent != null)
                parent = GameObjectResolver.ResolveOrThrow(rawParent as string).transform;

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset);
            Undo.RegisterCreatedObjectUndo(instance, "Bridge: Instantiate Prefab");

            if (parent != null) instance.transform.SetParent(parent, worldPositionStays: false);

            string requestedName = MutationBody.GetString(body, "name");
            instance.name = !string.IsNullOrEmpty(requestedName) ? requestedName : prefabAsset.name;

            instance.transform.localPosition = TransformParamReader.ReadVector3(body, "position", Vector3.zero);
            instance.transform.localRotation = TransformParamReader.ReadQuaternion(body, "rotation", Quaternion.identity);
            instance.transform.localScale = TransformParamReader.ReadVector3(body, "scale", Vector3.one);

            bool autoSaved = MutationAutoSave.SaveIfEnabled(instance);

            var responseBody = new Dictionary<string, object>
            {
                { "tier", "act" }, { "autoSaved", autoSaved }, { "object", MutationNodeBuilder.BuildNode(instance) }
            };
            return new BridgeHandlerResult { Status = 200, Body = responseBody };
        }

        // Object/prefab-asset resolution section (LOCKED): resolve via
        // AssetDatabase.GUIDToAssetPath + AssetDatabase.LoadAssetAtPath —
        // 400 invalid_prefab_guid if unresolvable OR if it resolves to
        // something that isn't itself a prefab asset (e.g. a scene asset's
        // GUID passed by mistake).
        static GameObject ResolvePrefabAssetOrThrow(string prefabGuid)
        {
            string path = AssetDatabase.GUIDToAssetPath(prefabGuid);
            GameObject asset = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null || PrefabUtility.GetPrefabAssetType(asset) == PrefabAssetType.NotAPrefab)
            {
                throw new MutationRejection(400, new Dictionary<string, object>
                {
                    { "tier", "act" }, { "error", "invalid_prefab_guid" }, { "prefabGuid", prefabGuid }
                });
            }
            return asset;
        }
    }
}
