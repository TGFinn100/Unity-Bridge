using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityBridge.Editor
{
    // POST /act/component/add (v2 LOCKED).
    internal static class ComponentAddEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "POST",
                Path = "/act/component/add",
                TopicKey = "act-component-add",
                Tier = "act",
                Synchronous = true,
                Summary = "Add a component to a GameObject",
                Params = new[]
                {
                    "id (string, required): GlobalObjectId of the target GameObject",
                    "type (string, required): component type name — short name (e.g. \"Rigidbody\"), or a fully-qualified name to disambiguate after a 400 ambiguous_type"
                },
                ExampleRequest = "POST /act/component/add {\"id\":\"...\",\"type\":\"Rigidbody\"} (header X-Bridge-Token: <token>)",
                ExampleResponseAbbrev = "{\"tier\":\"act\",\"autoSaved\":true,\"added\":{\"type\":\"Rigidbody\",\"fields\":{...}},\"autoAddedDependencies\":[]}",
                TimeoutMs = 5000,
                BuildMutation = Build
            });
        }

        static BridgeHandlerResult Build(Dictionary<string, object> body)
        {
            body = body ?? new Dictionary<string, object>();
            var go = MutationBody.ResolveIdOrThrow(body);
            var componentType = ComponentTypeResolver.ResolveOrThrow(MutationBody.GetString(body, "type"));

            // Snapshot before adding so any [RequireComponent]-driven
            // auto-add can be told apart from what was already there
            // (LOCKED: "surfaces any component Unity auto-added via
            // [RequireComponent]").
            var before = new HashSet<Component>(go.GetComponents<Component>());

            // Undo.AddComponent both adds and registers undo in one call
            // (LOCKED Undo table) — it also drives Unity's own
            // [RequireComponent] auto-add machinery, same as
            // GameObject.AddComponent would, so dependencies come along for
            // free.
            Component added = Undo.AddComponent(go, componentType);

            var autoAddedDependencies = new List<object>();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null || c == added || before.Contains(c)) continue;
                autoAddedDependencies.Add(new Dictionary<string, object>
                {
                    { "type", c.GetType().Name }, { "fields", SerializedValueExtractor.ExtractFields(c) }
                });
            }

            bool autoSaved = MutationAutoSave.SaveIfEnabled(go);

            var responseBody = new Dictionary<string, object>
            {
                { "tier", "act" }, { "autoSaved", autoSaved },
                { "added", new Dictionary<string, object> { { "type", added.GetType().Name }, { "fields", SerializedValueExtractor.ExtractFields(added) } } },
                { "autoAddedDependencies", autoAddedDependencies }
            };
            return new BridgeHandlerResult { Status = 200, Body = responseBody };
        }
    }
}
