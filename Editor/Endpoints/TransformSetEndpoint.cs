using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityBridge.Editor
{
    // POST /act/transform/set (v2.5 LOCKED). Local space only — matches
    // Transform.localPosition/localRotation/localScale and create/
    // instantiate's own params. Every field independently optional; unlike
    // create/instantiate's lenient per-component fallback
    // (TransformParamReader), a *supplied* field here is validated strictly
    // (all its components required) — a partial write silently keeping some
    // axes at their old value while others were sent would be confusing, so
    // "supplied" means "fully specified" for this endpoint.
    internal static class TransformSetEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "POST",
                Path = "/act/transform/set",
                TopicKey = "act-transform-set",
                Tier = "act",
                Synchronous = true,
                Summary = "Set local position/rotation/scale on a GameObject",
                Params = new[]
                {
                    "id (string, required): GlobalObjectId of the target GameObject",
                    "position (object {x,y,z}, optional): local position — omitted keeps current value",
                    "rotation (object {x,y,z,w}, optional): local rotation as a Quaternion — omitted keeps current value",
                    "scale (object {x,y,z}, optional): local scale — omitted keeps current value"
                },
                ExampleRequest = "POST /act/transform/set {\"id\":\"...\",\"position\":{\"x\":1,\"y\":0,\"z\":0}} (header X-Bridge-Token: <token>)",
                ExampleResponseAbbrev = "{\"tier\":\"act\",\"autoSaved\":true,\"object\":{\"id\":\"...\",\"name\":\"Cube\",\"active\":true,\"componentNames\":[\"Transform\"],\"components\":[{\"type\":\"Transform\",\"fields\":{...}}]}}",
                TimeoutMs = 5000,
                BuildMutation = Build
            });
        }

        static BridgeHandlerResult Build(Dictionary<string, object> body)
        {
            body = body ?? new Dictionary<string, object>();
            var go = MutationBody.ResolveIdOrThrow(body);

            bool hasPosition = body.TryGetValue("position", out var rawPosition) && rawPosition != null;
            bool hasRotation = body.TryGetValue("rotation", out var rawRotation) && rawRotation != null;
            bool hasScale = body.TryGetValue("scale", out var rawScale) && rawScale != null;

            if (!hasPosition && !hasRotation && !hasScale)
                throw new MutationRejection(400, new Dictionary<string, object> { { "tier", "act" }, { "error", "no_fields" } });

            Undo.RecordObject(go.transform, "Bridge: Set Transform");

            if (hasPosition) go.transform.localPosition = ReadVector3Strict(rawPosition, "position");
            if (hasRotation) go.transform.localRotation = ReadQuaternionStrict(rawRotation, "rotation");
            if (hasScale) go.transform.localScale = ReadVector3Strict(rawScale, "scale");

            bool autoSaved = MutationAutoSave.SaveIfEnabled(go);

            var responseBody = new Dictionary<string, object>
            {
                { "tier", "act" }, { "autoSaved", autoSaved }, { "object", MutationNodeBuilder.BuildNode(go) }
            };
            return new BridgeHandlerResult { Status = 200, Body = responseBody };
        }

        static Vector3 ReadVector3Strict(object raw, string fieldName)
        {
            var d = ExpectDict(raw, fieldName);
            return new Vector3(
                (float)ExpectComponent(d, "x", fieldName), (float)ExpectComponent(d, "y", fieldName), (float)ExpectComponent(d, "z", fieldName));
        }

        static Quaternion ReadQuaternionStrict(object raw, string fieldName)
        {
            var d = ExpectDict(raw, fieldName);
            return new Quaternion(
                (float)ExpectComponent(d, "x", fieldName), (float)ExpectComponent(d, "y", fieldName),
                (float)ExpectComponent(d, "z", fieldName), (float)ExpectComponent(d, "w", fieldName));
        }

        static Dictionary<string, object> ExpectDict(object raw, string fieldName)
        {
            if (!(raw is Dictionary<string, object> d))
            {
                throw new MutationRejection(400, new Dictionary<string, object>
                {
                    { "tier", "act" }, { "error", "type_mismatch" }, { "detail", $"{fieldName}: expected a JSON object" }
                });
            }
            return d;
        }

        static double ExpectComponent(Dictionary<string, object> d, string key, string fieldName)
        {
            if (!d.TryGetValue(key, out var v) || v == null)
            {
                throw new MutationRejection(400, new Dictionary<string, object>
                {
                    { "tier", "act" }, { "error", "type_mismatch" }, { "detail", $"{fieldName}: missing \"{key}\"" }
                });
            }
            if (!(v is int || v is long || v is double || v is float))
            {
                throw new MutationRejection(400, new Dictionary<string, object>
                {
                    { "tier", "act" }, { "error", "type_mismatch" }, { "detail", $"{fieldName}.{key} must be a number" }
                });
            }
            return JsonNum.ToDouble(v);
        }
    }
}
