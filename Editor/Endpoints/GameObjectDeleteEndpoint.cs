using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityBridge.Editor
{
    // POST /act/gameobject/delete (v2 LOCKED). Blast-radius info is child
    // count + prefab-instance-root flag only — cross-object reference
    // scanning ("does anything else in the scene reference this object")
    // was scoped out of v2 (would require a full scene scan per delete
    // call), per the brief's explicit narrowing from FINAL.md's original,
    // broader ask.
    internal static class GameObjectDeleteEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "POST",
                Path = "/act/gameobject/delete",
                TopicKey = "act-gameobject-delete",
                Tier = "act",
                Synchronous = true,
                Summary = "Delete a GameObject, optionally with children",
                Params = new[]
                {
                    "id (string, required): GlobalObjectId of the object to delete",
                    "recursive (bool, optional): default false — required true if the object has children, or delete is rejected"
                },
                ExampleRequest = "POST /act/gameobject/delete {\"id\":\"...\",\"recursive\":true} (header X-Bridge-Token: <token>)",
                ExampleResponseAbbrev = "{\"tier\":\"act\",\"autoSaved\":true,\"deleted\":{\"id\":\"...\",\"name\":\"Cube\",\"childCount\":0,\"wasPrefabInstanceRoot\":false}}",
                TimeoutMs = 5000,
                BuildMutation = Build
            });
        }

        static BridgeHandlerResult Build(Dictionary<string, object> body)
        {
            body = body ?? new Dictionary<string, object>();
            var go = MutationBody.ResolveIdOrThrow(body);
            bool recursive = MutationBody.GetBool(body, "recursive", false);

            int childCount = CountDescendants(go.transform);
            if (childCount > 0 && !recursive)
            {
                string childNoun = childCount == 1 ? "child" : "children";
                throw new MutationRejection(409, new Dictionary<string, object>
                {
                    { "tier", "act" }, { "error", "has_children" }, { "childCount", childCount },
                    { "hint", $"pass recursive:true to delete this object and its {childCount} {childNoun}" }
                });
            }

            // Captured before destruction — nothing on a destroyed
            // UnityEngine.Object is safe to read afterward.
            string id = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString();
            string name = go.name;
            bool wasPrefabInstanceRoot = PrefabUtility.IsOutermostPrefabInstanceRoot(go);
            Scene scene = go.scene;

            Undo.DestroyObjectImmediate(go);

            bool autoSaved = MutationAutoSave.SaveIfEnabled(scene);

            var responseBody = new Dictionary<string, object>
            {
                { "tier", "act" }, { "autoSaved", autoSaved },
                { "deleted", new Dictionary<string, object>
                    {
                        { "id", id }, { "name", name }, { "childCount", childCount }, { "wasPrefabInstanceRoot", wasPrefabInstanceRoot }
                    }
                }
            };
            return new BridgeHandlerResult { Status = 200, Body = responseBody };
        }

        static int CountDescendants(Transform t)
        {
            int count = t.childCount;
            for (int i = 0; i < t.childCount; i++) count += CountDescendants(t.GetChild(i));
            return count;
        }
    }
}
