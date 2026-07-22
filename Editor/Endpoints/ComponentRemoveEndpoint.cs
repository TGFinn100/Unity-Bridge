using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityBridge.Editor
{
    // POST /act/component/remove (v2 LOCKED). The required_by_dependency
    // check is proactive — scans the GameObject's other components for
    // [RequireComponent] attributes referencing the target type BEFORE
    // attempting removal, because Unity's own DestroyImmediate on a
    // required component logs a Console error and silently no-ops rather
    // than throwing; relying on that would look like a false success over
    // HTTP (LOCKED rationale).
    internal static class ComponentRemoveEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "POST",
                Path = "/act/component/remove",
                TopicKey = "act-component-remove",
                Tier = "act",
                Synchronous = true,
                Summary = "Remove a component from a GameObject",
                Params = new[]
                {
                    "id (string, required): GlobalObjectId of the target GameObject",
                    "type (string, required): component type name to remove"
                },
                ExampleRequest = "POST /act/component/remove {\"id\":\"...\",\"type\":\"Rigidbody\"} (header X-Bridge-Token: <token>)",
                ExampleResponseAbbrev = "{\"tier\":\"act\",\"autoSaved\":true,\"removed\":{\"type\":\"Rigidbody\"}}",
                TimeoutMs = 5000,
                BuildMutation = Build
            });
        }

        static BridgeHandlerResult Build(Dictionary<string, object> body)
        {
            body = body ?? new Dictionary<string, object>();
            var go = MutationBody.ResolveIdOrThrow(body);
            string typeName = MutationBody.GetString(body, "type");
            var componentType = ComponentTypeResolver.ResolveOrThrow(typeName);

            Component target = go.GetComponent(componentType);
            if (target == null)
            {
                throw new MutationRejection(404, new Dictionary<string, object>
                {
                    { "tier", "act" }, { "error", "component_not_found" }, { "type", typeName }
                });
            }

            var blockedBy = new List<object>();
            foreach (var other in go.GetComponents<Component>())
            {
                if (other == null || other == target) continue;
                foreach (var attrObj in other.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                {
                    if (attrObj is RequireComponent req && RequirementBlocks(req, target.GetType()))
                        blockedBy.Add(other.GetType().Name);
                }
            }

            if (blockedBy.Count > 0)
            {
                throw new MutationRejection(409, new Dictionary<string, object>
                {
                    { "tier", "act" }, { "error", "required_by_dependency" }, { "blockedBy", blockedBy }
                });
            }

            Undo.DestroyObjectImmediate(target);
            bool autoSaved = MutationAutoSave.SaveIfEnabled(go);

            var responseBody = new Dictionary<string, object>
            {
                { "tier", "act" }, { "autoSaved", autoSaved },
                { "removed", new Dictionary<string, object> { { "type", typeName } } }
            };
            return new BridgeHandlerResult { Status = 200, Body = responseBody };
        }

        static bool RequirementBlocks(RequireComponent req, System.Type targetType) =>
            (req.m_Type0 != null && req.m_Type0.IsAssignableFrom(targetType)) ||
            (req.m_Type1 != null && req.m_Type1.IsAssignableFrom(targetType)) ||
            (req.m_Type2 != null && req.m_Type2.IsAssignableFrom(targetType));
    }
}
