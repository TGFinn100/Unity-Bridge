using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityBridge.Editor
{
    // POST /act/component/set-field (v2 LOCKED). Write-side mirror of
    // SerializedValueExtractor's read-side switch — only the types listed
    // in the brief's "Field/value write scope" are supported; anything else
    // (arrays, lists, generic/nested fields — the read side's "<Type>"
    // placeholder cases) is out of scope for v2, no existing read-side
    // precedent to build a write path from.
    internal static class ComponentSetFieldEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "POST",
                Path = "/act/component/set-field",
                TopicKey = "act-component-set-field",
                Tier = "act",
                Synchronous = true,
                Summary = "Set one field on a component",
                Params = new[]
                {
                    "id (string, required): GlobalObjectId of the target GameObject",
                    "component (string, required): component type name",
                    "field (string, required): top-level field name, from /object/{id}?components=values",
                    "value (JSON, required): encoded per the field's type — Integer/Boolean/Float/String as JSON primitives; Enum as its display-name string (or raw index int); Color as {r,g,b,a?}; Vector2/3/4 as {x,y,z?,w?}; Vector2Int/3Int as {x,y,z?}; Rect as {x,y,width,height}; Quaternion as {x,y,z,w} (all four required); ObjectReference as {\"assetGuid\":...}, {\"objectId\":...}, or null to clear"
                },
                ExampleRequest = "POST /act/component/set-field {\"id\":\"...\",\"component\":\"Rigidbody\",\"field\":\"mass\",\"value\":5} (header X-Bridge-Token: <token>)",
                ExampleResponseAbbrev = "{\"tier\":\"act\",\"autoSaved\":true,\"updated\":{\"type\":\"Rigidbody\",\"fields\":{\"mass\":5,...}}}",
                TimeoutMs = 5000,
                BuildMutation = Build
            });
        }

        static BridgeHandlerResult Build(Dictionary<string, object> body)
        {
            body = body ?? new Dictionary<string, object>();
            var go = MutationBody.ResolveIdOrThrow(body);

            string componentTypeName = MutationBody.GetString(body, "component");
            var componentType = ComponentTypeResolver.ResolveOrThrow(componentTypeName);

            Component component = go.GetComponent(componentType);
            if (component == null)
            {
                throw new MutationRejection(404, new Dictionary<string, object>
                {
                    { "tier", "act" }, { "error", "component_not_found" }, { "type", componentTypeName }
                });
            }

            string fieldName = MutationBody.GetString(body, "field");
            // m_Script is deliberately excluded from the read-side field
            // vocabulary (SerializedValueExtractor skips it) — a caller
            // could never have learned this name from /object/{id}, and
            // writing it would reassign the component's script class.
            var so = new SerializedObject(component);
            SerializedProperty prop = string.IsNullOrEmpty(fieldName) || fieldName == "m_Script"
                ? null
                : so.FindProperty(fieldName);
            if (prop == null)
            {
                throw new MutationRejection(400, new Dictionary<string, object>
                {
                    { "tier", "act" }, { "error", "unknown_field" }, { "field", fieldName }
                });
            }

            body.TryGetValue("value", out var rawValue);

            Undo.RecordObject(component, "Bridge: Set Field");
            WriteValue(prop, rawValue, fieldName);
            so.ApplyModifiedProperties();

            bool autoSaved = MutationAutoSave.SaveIfEnabled(go);

            var responseBody = new Dictionary<string, object>
            {
                { "tier", "act" }, { "autoSaved", autoSaved },
                { "updated", new Dictionary<string, object> { { "type", component.GetType().Name }, { "fields", SerializedValueExtractor.ExtractFields(component) } } }
            };
            return new BridgeHandlerResult { Status = 200, Body = responseBody };
        }

        static void WriteValue(SerializedProperty prop, object jsonValue, string fieldName)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    prop.intValue = (int)ExpectNumber(jsonValue, fieldName);
                    break;
                case SerializedPropertyType.Boolean:
                    prop.boolValue = ExpectBool(jsonValue, fieldName);
                    break;
                case SerializedPropertyType.Float:
                    prop.floatValue = (float)ExpectNumber(jsonValue, fieldName);
                    break;
                case SerializedPropertyType.String:
                    prop.stringValue = ExpectString(jsonValue, fieldName);
                    break;
                case SerializedPropertyType.Enum:
                    prop.enumValueIndex = ResolveEnumIndex(prop, jsonValue, fieldName);
                    break;
                case SerializedPropertyType.Color:
                    {
                        var d = ExpectDict(jsonValue, fieldName);
                        prop.colorValue = new Color(
                            (float)ExpectComponent(d, "r", fieldName),
                            (float)ExpectComponent(d, "g", fieldName),
                            (float)ExpectComponent(d, "b", fieldName),
                            (float)ExpectComponent(d, "a", fieldName, required: false, fallback: 1));
                        break;
                    }
                case SerializedPropertyType.ObjectReference:
                    prop.objectReferenceValue = ReadObjectReference(jsonValue, fieldName);
                    break;
                case SerializedPropertyType.Vector2:
                    {
                        var d = ExpectDict(jsonValue, fieldName);
                        prop.vector2Value = new Vector2((float)ExpectComponent(d, "x", fieldName), (float)ExpectComponent(d, "y", fieldName));
                        break;
                    }
                case SerializedPropertyType.Vector3:
                    {
                        var d = ExpectDict(jsonValue, fieldName);
                        prop.vector3Value = new Vector3(
                            (float)ExpectComponent(d, "x", fieldName), (float)ExpectComponent(d, "y", fieldName), (float)ExpectComponent(d, "z", fieldName));
                        break;
                    }
                case SerializedPropertyType.Vector4:
                    {
                        var d = ExpectDict(jsonValue, fieldName);
                        prop.vector4Value = new Vector4(
                            (float)ExpectComponent(d, "x", fieldName), (float)ExpectComponent(d, "y", fieldName),
                            (float)ExpectComponent(d, "z", fieldName), (float)ExpectComponent(d, "w", fieldName));
                        break;
                    }
                case SerializedPropertyType.Quaternion:
                    {
                        // v2.5 foundational fix — write-side mirror of the
                        // read-side {x,y,z,w} encoding added to
                        // SerializedValueExtractor. "w" is required, unlike
                        // Vector3/Vector4's optional trailing components,
                        // since a quaternion missing w isn't a meaningful
                        // partial value (LOCKED: 400 type_mismatch "rotation
                        // missing w").
                        var d = ExpectDict(jsonValue, fieldName);
                        prop.quaternionValue = new Quaternion(
                            (float)ExpectComponent(d, "x", fieldName), (float)ExpectComponent(d, "y", fieldName),
                            (float)ExpectComponent(d, "z", fieldName), (float)ExpectComponent(d, "w", fieldName));
                        break;
                    }
                case SerializedPropertyType.Vector2Int:
                    {
                        var d = ExpectDict(jsonValue, fieldName);
                        prop.vector2IntValue = new Vector2Int((int)ExpectComponent(d, "x", fieldName), (int)ExpectComponent(d, "y", fieldName));
                        break;
                    }
                case SerializedPropertyType.Vector3Int:
                    {
                        var d = ExpectDict(jsonValue, fieldName);
                        prop.vector3IntValue = new Vector3Int(
                            (int)ExpectComponent(d, "x", fieldName), (int)ExpectComponent(d, "y", fieldName), (int)ExpectComponent(d, "z", fieldName));
                        break;
                    }
                case SerializedPropertyType.Rect:
                    {
                        var d = ExpectDict(jsonValue, fieldName);
                        prop.rectValue = new Rect(
                            (float)ExpectComponent(d, "x", fieldName), (float)ExpectComponent(d, "y", fieldName),
                            (float)ExpectComponent(d, "width", fieldName), (float)ExpectComponent(d, "height", fieldName));
                        break;
                    }
                default:
                    throw new MutationRejection(400, new Dictionary<string, object>
                    {
                        { "tier", "act" }, { "error", "unsupported_field_type" }, { "field", fieldName }, { "propertyType", prop.propertyType.ToString() }
                    });
            }
        }

        static int ResolveEnumIndex(SerializedProperty prop, object jsonValue, string fieldName)
        {
            if (jsonValue is string s)
            {
                int idx = System.Array.IndexOf(prop.enumDisplayNames, s);
                if (idx < 0)
                {
                    throw new MutationRejection(400, new Dictionary<string, object>
                    {
                        { "tier", "act" }, { "error", "type_mismatch" },
                        { "detail", $"{fieldName}: \"{s}\" is not a valid enum value — expected one of [{string.Join(", ", prop.enumDisplayNames)}]" }
                    });
                }
                return idx;
            }
            if (jsonValue is int || jsonValue is long || jsonValue is double)
            {
                int idx = (int)JsonNum.ToLong(jsonValue);
                if (idx < 0 || idx >= prop.enumDisplayNames.Length)
                {
                    throw new MutationRejection(400, new Dictionary<string, object>
                    {
                        { "tier", "act" }, { "error", "type_mismatch" }, { "detail", $"{fieldName}: enum index {idx} out of range" }
                    });
                }
                return idx;
            }
            throw new MutationRejection(400, new Dictionary<string, object>
            {
                { "tier", "act" }, { "error", "type_mismatch" }, { "detail", $"{fieldName}: expected an enum display-name string or an index int" }
            });
        }

        static Object ReadObjectReference(object jsonValue, string fieldName)
        {
            if (jsonValue == null) return null; // "or null to clear the reference" (LOCKED)

            var d = ExpectDict(jsonValue, fieldName);

            if (d.TryGetValue("assetGuid", out var guidObj) && guidObj is string guid)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Object>(path);
                if (asset == null)
                {
                    throw new MutationRejection(400, new Dictionary<string, object>
                    {
                        { "tier", "act" }, { "error", "type_mismatch" }, { "detail", $"{fieldName}: assetGuid \"{guid}\" does not resolve to a loadable asset" }
                    });
                }
                return asset;
            }

            if (d.TryGetValue("objectId", out var idObj) && idObj is string objectId)
            {
                if (!GlobalObjectId.TryParse(objectId, out GlobalObjectId gid))
                {
                    throw new MutationRejection(400, new Dictionary<string, object>
                    {
                        { "tier", "act" }, { "error", "type_mismatch" }, { "detail", $"{fieldName}: objectId is not a valid GlobalObjectId" }
                    });
                }
                var entityId = GlobalObjectId.GlobalObjectIdentifierToEntityIdSlow(gid);
                var obj = EditorUtility.EntityIdToObject(entityId);
                if (obj == null)
                {
                    throw new MutationRejection(400, new Dictionary<string, object>
                    {
                        { "tier", "act" }, { "error", "type_mismatch" }, { "detail", $"{fieldName}: objectId is stale — object gone or state reloaded; re-discover, don't retry" }
                    });
                }
                return obj;
            }

            throw new MutationRejection(400, new Dictionary<string, object>
            {
                { "tier", "act" }, { "error", "type_mismatch" }, { "detail", $"{fieldName}: expected {{\"assetGuid\":...}}, {{\"objectId\":...}}, or null" }
            });
        }

        static Dictionary<string, object> ExpectDict(object jsonValue, string fieldName)
        {
            if (!(jsonValue is Dictionary<string, object> d))
            {
                throw new MutationRejection(400, new Dictionary<string, object>
                {
                    { "tier", "act" }, { "error", "type_mismatch" }, { "detail", $"{fieldName}: expected a JSON object" }
                });
            }
            return d;
        }

        static double ExpectComponent(Dictionary<string, object> d, string key, string fieldName, bool required = true, double fallback = 0)
        {
            if (!d.TryGetValue(key, out var v) || v == null)
            {
                if (!required) return fallback;
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

        static double ExpectNumber(object jsonValue, string fieldName)
        {
            if (!(jsonValue is int || jsonValue is long || jsonValue is double || jsonValue is float))
            {
                throw new MutationRejection(400, new Dictionary<string, object>
                {
                    { "tier", "act" }, { "error", "type_mismatch" }, { "detail", $"{fieldName}: expected a number" }
                });
            }
            return JsonNum.ToDouble(jsonValue);
        }

        static bool ExpectBool(object jsonValue, string fieldName)
        {
            if (!(jsonValue is bool b))
            {
                throw new MutationRejection(400, new Dictionary<string, object>
                {
                    { "tier", "act" }, { "error", "type_mismatch" }, { "detail", $"{fieldName}: expected true or false" }
                });
            }
            return b;
        }

        static string ExpectString(object jsonValue, string fieldName)
        {
            if (!(jsonValue is string s))
            {
                throw new MutationRejection(400, new Dictionary<string, object>
                {
                    { "tier", "act" }, { "error", "type_mismatch" }, { "detail", $"{fieldName}: expected a string" }
                });
            }
            return s;
        }
    }
}
