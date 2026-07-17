using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityBridge.Editor
{
    // Converts a Component's serialized fields into JSON-safe primitives for
    // GET /object/{id}?components=values. Only ever called for a single
    // object's own components (never recursively over a subtree — see
    // ObjectEndpoint), so no size cap lives here; the caller's overall
    // 16KB trim handles that.
    internal static class SerializedValueExtractor
    {
        internal static Dictionary<string, object> ExtractFields(Component component)
        {
            var fields = new Dictionary<string, object>();
            var so = new SerializedObject(component);
            var prop = so.GetIterator();

            bool enterChildren = true;
            while (prop.NextVisible(enterChildren))
            {
                enterChildren = false; // only descend into the first (root) property; every property after that is a top-level field, never its own children (that's what keeps Vector3/Color from exploding into x/y/z/r/g/b/a entries)
                if (prop.name == "m_Script") continue;
                fields[prop.name] = ConvertValue(prop);
            }

            return fields;
        }

        static object ConvertValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return prop.intValue;
                case SerializedPropertyType.Boolean:
                    return prop.boolValue;
                case SerializedPropertyType.Float:
                    return prop.floatValue;
                case SerializedPropertyType.String:
                    return prop.stringValue;
                case SerializedPropertyType.Enum:
                    return prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumDisplayNames.Length
                        ? prop.enumDisplayNames[prop.enumValueIndex]
                        : prop.enumValueIndex;
                case SerializedPropertyType.Color:
                    {
                        var c = prop.colorValue;
                        return new Dictionary<string, object> { { "r", c.r }, { "g", c.g }, { "b", c.b }, { "a", c.a } };
                    }
                case SerializedPropertyType.ObjectReference:
                    {
                        var obj = prop.objectReferenceValue;
                        return obj == null ? null : new Dictionary<string, object> { { "refName", obj.name }, { "refType", obj.GetType().Name } };
                    }
                case SerializedPropertyType.Vector2:
                    {
                        var v = prop.vector2Value;
                        return new Dictionary<string, object> { { "x", v.x }, { "y", v.y } };
                    }
                case SerializedPropertyType.Vector3:
                    {
                        var v = prop.vector3Value;
                        return new Dictionary<string, object> { { "x", v.x }, { "y", v.y }, { "z", v.z } };
                    }
                case SerializedPropertyType.Vector4:
                    {
                        var v = prop.vector4Value;
                        return new Dictionary<string, object> { { "x", v.x }, { "y", v.y }, { "z", v.z }, { "w", v.w } };
                    }
                case SerializedPropertyType.Vector2Int:
                    {
                        var v = prop.vector2IntValue;
                        return new Dictionary<string, object> { { "x", v.x }, { "y", v.y } };
                    }
                case SerializedPropertyType.Vector3Int:
                    {
                        var v = prop.vector3IntValue;
                        return new Dictionary<string, object> { { "x", v.x }, { "y", v.y }, { "z", v.z } };
                    }
                case SerializedPropertyType.Rect:
                    {
                        var r = prop.rectValue;
                        return new Dictionary<string, object> { { "x", r.x }, { "y", r.y }, { "width", r.width }, { "height", r.height } };
                    }
                case SerializedPropertyType.ArraySize:
                    return prop.intValue;
                default:
                    // Generic/nested/array/gradient/curve/etc. — not worth a
                    // full recursive serializer for v1 (task brief: "always
                    // depth-limited and size-capped", and an unbounded array
                    // could blow the response on its own). A named
                    // placeholder still tells the agent the field exists.
                    return $"<{prop.propertyType}>";
            }
        }
    }
}
