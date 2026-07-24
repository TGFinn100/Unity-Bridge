using System.Collections.Generic;
using UnityEditor;

namespace UnityBridge.Editor
{
    // POST /act/prefab/revert (v2.5 LOCKED). Whole-instance revert
    // (PrefabUtility.RevertPrefabInstance) — scene-side only (discards the
    // instance's overrides, no asset-file write). CONFIRMED live via a
    // human-driven Ctrl+Z check (per the pre-build gap-check's resolution —
    // no scripted Undo.PerformUndo() check, same as v2's own
    // gate2-mutation.sh): after set-field then revert, Ctrl+Z restored the
    // pre-revert override value exactly — ordinary native Undo coverage.
    // Per the brief's own rule, "undoable" is therefore dropped entirely
    // here rather than kept as an always-true, uninformative field —
    // matching instantiate/transform-set/save's own omit-when-constant
    // convention. Logged in the package README's DECISIONS heading.
    internal static class PrefabRevertEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "POST",
                Path = "/act/prefab/revert",
                TopicKey = "act-prefab-revert",
                Tier = "act",
                Synchronous = true,
                Summary = "Revert a prefab instance's overrides",
                Params = new[]
                {
                    "id (string, required): GlobalObjectId of the connected prefab instance"
                },
                ExampleRequest = "POST /act/prefab/revert {\"id\":\"...\"} (header X-Bridge-Token: <token>)",
                ExampleResponseAbbrev = "{\"tier\":\"act\",\"autoSaved\":true,\"object\":{\"id\":\"...\",\"name\":\"Foo\",\"active\":true,\"componentNames\":[\"Transform\"],\"components\":[{\"type\":\"Transform\",\"fields\":{...}}]}}",
                TimeoutMs = 5000,
                BuildMutation = Build
            });
        }

        static BridgeHandlerResult Build(Dictionary<string, object> body)
        {
            body = body ?? new Dictionary<string, object>();
            var go = MutationBody.ResolveIdOrThrow(body);

            PrefabConnectionResolver.RequireConnectedInstance(go, out _, out _);

            Undo.RegisterCompleteObjectUndo(go, "Bridge: Revert Prefab Overrides");
            PrefabUtility.RevertPrefabInstance(go, InteractionMode.AutomatedAction);

            bool autoSaved = MutationAutoSave.SaveIfEnabled(go);

            var responseBody = new Dictionary<string, object>
            {
                { "tier", "act" }, { "autoSaved", autoSaved },
                { "object", MutationNodeBuilder.BuildNode(go) }
            };
            return new BridgeHandlerResult { Status = 200, Body = responseBody };
        }
    }
}
