using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityBridge.Editor
{
    // POST /act/gameobject/create (v2 LOCKED). Synchronous mutation — see
    // routing-and-tiers.md's "Synchronous mutation dispatch (v2)" section
    // for why this is Tier="act"+Synchronous=true rather than the existing
    // 202-then-Tick act pattern.
    internal static class GameObjectCreateEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "POST",
                Path = "/act/gameobject/create",
                TopicKey = "act-gameobject-create",
                Tier = "act",
                Synchronous = true,
                Summary = "Create a new GameObject in the scene",
                Params = new[]
                {
                    "name (string, required): new GameObject's name",
                    "parent (string, optional): GlobalObjectId to parent under; default/null = scene root",
                    "position (object {x,y,z}, optional): local position, default zero",
                    "rotation (object {x,y,z,w}, optional): local rotation as a Quaternion — migrated from Euler {x,y,z} in v2.5 (see SerializedValueExtractor's Quaternion support) — default identity {0,0,0,1}",
                    "scale (object {x,y,z}, optional): local scale, default one"
                },
                ExampleRequest = "POST /act/gameobject/create {\"name\":\"Cube\"} (header X-Bridge-Token: <token>)",
                ExampleResponseAbbrev = "{\"tier\":\"act\",\"autoSaved\":true,\"object\":{\"id\":\"...\",\"name\":\"Cube\",\"active\":true,\"componentNames\":[\"Transform\"],\"components\":[{\"type\":\"Transform\",\"fields\":{...}}]}}",
                TimeoutMs = 5000,
                BuildMutation = Build
            });
        }

        static BridgeHandlerResult Build(Dictionary<string, object> body)
        {
            body = body ?? new Dictionary<string, object>();

            string name = MutationBody.GetString(body, "name");
            if (string.IsNullOrEmpty(name))
                throw new MutationRejection(400, new Dictionary<string, object> { { "tier", "act" }, { "error", "missing_name" } });

            // create's own default differs from duplicate/reparent's
            // "keep current parent"/fallback semantics — omitted or
            // explicit null both mean scene root here (LOCKED: "default
            // null = scene root"), so a plain GetString-and-resolve is
            // enough; no need for ResolveOptionalParent's omitted-vs-null
            // distinction.
            Transform parent = null;
            string parentId = MutationBody.GetString(body, "parent");
            if (body.TryGetValue("parent", out var rawParent) && rawParent != null)
                parent = GameObjectResolver.ResolveOrThrow(parentId).transform;

            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Bridge: Create GameObject");

            if (parent != null) go.transform.SetParent(parent, worldPositionStays: false);

            go.transform.localPosition = TransformParamReader.ReadVector3(body, "position", Vector3.zero);
            go.transform.localRotation = TransformParamReader.ReadQuaternion(body, "rotation", Quaternion.identity);
            go.transform.localScale = TransformParamReader.ReadVector3(body, "scale", Vector3.one);

            bool autoSaved = MutationAutoSave.SaveIfEnabled(go);

            var responseBody = new Dictionary<string, object>
            {
                { "tier", "act" }, { "autoSaved", autoSaved }, { "object", MutationNodeBuilder.BuildNode(go) }
            };
            return new BridgeHandlerResult { Status = 200, Body = responseBody };
        }
    }
}
