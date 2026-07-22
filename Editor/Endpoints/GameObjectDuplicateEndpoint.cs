using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityBridge.Editor
{
    // POST /act/gameobject/duplicate (v2 LOCKED).
    internal static class GameObjectDuplicateEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "POST",
                Path = "/act/gameobject/duplicate",
                TopicKey = "act-gameobject-duplicate",
                Tier = "act",
                Synchronous = true,
                Summary = "Duplicate a GameObject and its components",
                Params = new[]
                {
                    "id (string, required): GlobalObjectId of the object to duplicate",
                    "parent (string, optional): GlobalObjectId to parent the copy under; default (key omitted) = keep original's current parent, explicit null = scene root",
                    "name (string, optional): default = Unity's own duplicate-naming convention (GameObjectUtility.GetUniqueNameForSibling)"
                },
                ExampleRequest = "POST /act/gameobject/duplicate {\"id\":\"...\"} (header X-Bridge-Token: <token>)",
                ExampleResponseAbbrev = "{\"tier\":\"act\",\"autoSaved\":true,\"object\":{\"id\":\"...\",\"name\":\"Cube (1)\",\"active\":true,\"componentNames\":[\"Transform\"],\"components\":[...]}}",
                TimeoutMs = 5000,
                BuildMutation = Build
            });
        }

        static BridgeHandlerResult Build(Dictionary<string, object> body)
        {
            body = body ?? new Dictionary<string, object>();
            var source = MutationBody.ResolveIdOrThrow(body);

            // "default: keep original's current parent" (LOCKED) — distinct
            // from an explicit null, which (by symmetry with reparent's own
            // id|null contract) means scene root.
            Transform parent = MutationBody.ResolveOptionalParent(body, fallback: source.transform.parent);

            var clone = Object.Instantiate(source);
            Undo.RegisterCreatedObjectUndo(clone, "Bridge: Duplicate GameObject");
            // worldPositionStays:false — the clone was instantiated with the
            // same local transform values as source; reparenting without
            // preserving world position keeps those numbers identical under
            // the (usually identical) parent, reproducing an exact
            // duplicate in the default case.
            clone.transform.SetParent(parent, worldPositionStays: false);

            string requestedName = MutationBody.GetString(body, "name");
            clone.name = !string.IsNullOrEmpty(requestedName)
                ? requestedName
                : GameObjectUtility.GetUniqueNameForSibling(parent, source.name);

            bool autoSaved = MutationAutoSave.SaveIfEnabled(clone);

            var responseBody = new Dictionary<string, object>
            {
                { "tier", "act" }, { "autoSaved", autoSaved }, { "object", MutationNodeBuilder.BuildNode(clone) }
            };
            return new BridgeHandlerResult { Status = 200, Body = responseBody };
        }
    }
}
