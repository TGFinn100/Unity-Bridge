using System.Collections.Generic;
using UnityEditor;

namespace UnityBridge.Editor
{
    // POST /act/prefab/apply (v2.5 LOCKED). Whole-instance apply
    // (PrefabUtility.ApplyPrefabInstance, all overrides at once) — matches
    // Unity's own Inspector "Overrides > Apply All". Not undoable:
    // PrefabUtility.ApplyPrefabInstance writes directly to the prefab
    // asset FILE; Unity's own scripting reference documents this operation
    // as not undoable. No best-effort Undo wrap is attempted (no hook
    // Unity provides to attach one to) — "undoable":false is always
    // present, per the LOCKED Undo integration table.
    internal static class PrefabApplyEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "POST",
                Path = "/act/prefab/apply",
                TopicKey = "act-prefab-apply",
                Tier = "act",
                Synchronous = true,
                Summary = "Apply a prefab instance's overrides to its asset",
                Params = new[]
                {
                    "id (string, required): GlobalObjectId of the connected prefab instance"
                },
                ExampleRequest = "POST /act/prefab/apply {\"id\":\"...\"} (header X-Bridge-Token: <token>)",
                ExampleResponseAbbrev = "{\"tier\":\"act\",\"autoSaved\":true,\"undoable\":false,\"applied\":{\"prefabGuid\":\"...\",\"prefabPath\":\"Assets/Prefabs/Foo.prefab\"},\"object\":{\"id\":\"...\",\"name\":\"Foo\",\"active\":true,\"componentNames\":[\"Transform\"],\"components\":[{\"type\":\"Transform\",\"fields\":{...}}]}}",
                TimeoutMs = 5000,
                BuildMutation = Build
            });
        }

        static BridgeHandlerResult Build(Dictionary<string, object> body)
        {
            body = body ?? new Dictionary<string, object>();
            var go = MutationBody.ResolveIdOrThrow(body);

            PrefabConnectionResolver.RequireConnectedInstance(go, out string prefabGuid, out string prefabPath);

            PrefabUtility.ApplyPrefabInstance(go, InteractionMode.AutomatedAction);

            bool autoSaved = MutationAutoSave.SaveIfEnabled(go);

            var responseBody = new Dictionary<string, object>
            {
                { "tier", "act" }, { "autoSaved", autoSaved }, { "undoable", false },
                { "applied", new Dictionary<string, object> { { "prefabGuid", prefabGuid }, { "prefabPath", prefabPath } } },
                { "object", MutationNodeBuilder.BuildNode(go) }
            };
            return new BridgeHandlerResult { Status = 200, Body = responseBody };
        }
    }
}
