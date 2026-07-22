using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityBridge.Editor
{
    // POST /act/gameobject/reparent (v2 LOCKED).
    internal static class GameObjectReparentEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "POST",
                Path = "/act/gameobject/reparent",
                TopicKey = "act-gameobject-reparent",
                Tier = "act",
                Synchronous = true,
                Summary = "Move a GameObject to a new parent",
                Params = new[]
                {
                    "id (string, required): GlobalObjectId of the object to move",
                    "parent (string, optional): GlobalObjectId of the new parent; null/omitted = move to scene root",
                    "worldPositionStays (bool, optional): default true, matches Transform.SetParent's own default"
                },
                ExampleRequest = "POST /act/gameobject/reparent {\"id\":\"...\",\"parent\":\"...\"} (header X-Bridge-Token: <token>)",
                ExampleResponseAbbrev = "{\"tier\":\"act\",\"autoSaved\":true,\"object\":{...},\"parentId\":\"...\"}",
                TimeoutMs = 5000,
                BuildMutation = Build
            });
        }

        static BridgeHandlerResult Build(Dictionary<string, object> body)
        {
            body = body ?? new Dictionary<string, object>();
            var go = MutationBody.ResolveIdOrThrow(body);

            // "null = move to scene root" (LOCKED) — an omitted key is
            // treated the same way, matching the fallback null passed here.
            Transform parent = MutationBody.ResolveOptionalParent(body, fallback: null);
            bool worldPositionStays = MutationBody.GetBool(body, "worldPositionStays", true);

            Undo.SetTransformParent(go.transform, parent, worldPositionStays, "Bridge: Reparent");

            bool autoSaved = MutationAutoSave.SaveIfEnabled(go);
            string parentId = parent == null ? null : GlobalObjectId.GetGlobalObjectIdSlow(parent.gameObject).ToString();

            var responseBody = new Dictionary<string, object>
            {
                { "tier", "act" }, { "autoSaved", autoSaved },
                { "object", MutationNodeBuilder.BuildNode(go) }, { "parentId", parentId }
            };
            return new BridgeHandlerResult { Status = 200, Body = responseBody };
        }
    }
}
