using System.Collections.Generic;
using UnityEditor;

namespace UnityBridge.Editor
{
    // POST /act/gameobject/rename (v2 LOCKED).
    internal static class GameObjectRenameEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "POST",
                Path = "/act/gameobject/rename",
                TopicKey = "act-gameobject-rename",
                Tier = "act",
                Synchronous = true,
                Summary = "Rename a GameObject",
                Params = new[]
                {
                    "id (string, required): GlobalObjectId of the object to rename",
                    "name (string, required, non-empty): new name"
                },
                ExampleRequest = "POST /act/gameobject/rename {\"id\":\"...\",\"name\":\"Player\"} (header X-Bridge-Token: <token>)",
                ExampleResponseAbbrev = "{\"tier\":\"act\",\"autoSaved\":true,\"object\":{\"id\":\"...\",\"name\":\"Player\",...}}",
                TimeoutMs = 5000,
                BuildMutation = Build
            });
        }

        static BridgeHandlerResult Build(Dictionary<string, object> body)
        {
            body = body ?? new Dictionary<string, object>();
            var go = MutationBody.ResolveIdOrThrow(body);

            string newName = MutationBody.GetString(body, "name");
            if (string.IsNullOrEmpty(newName))
                throw new MutationRejection(400, new Dictionary<string, object> { { "tier", "act" }, { "error", "missing_name" } });

            Undo.RecordObject(go, "Bridge: Rename");
            go.name = newName;

            bool autoSaved = MutationAutoSave.SaveIfEnabled(go);

            var responseBody = new Dictionary<string, object>
            {
                { "tier", "act" }, { "autoSaved", autoSaved }, { "object", MutationNodeBuilder.BuildNode(go) }
            };
            return new BridgeHandlerResult { Status = 200, Body = responseBody };
        }
    }
}
