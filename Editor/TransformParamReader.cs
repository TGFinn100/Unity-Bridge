using System.Collections.Generic;
using UnityEngine;

namespace UnityBridge.Editor
{
    // v2.5: shared position/rotation/scale body-reading, extracted from
    // GameObjectCreateEndpoint (v2) so /act/prefab/instantiate can reuse the
    // exact same defaults and per-component-fallback behavior instead of
    // duplicating it — same "pull out shared code" precedent as
    // ComponentSerializer/MutationNodeBuilder.
    internal static class TransformParamReader
    {
        internal static Vector3 ReadVector3(Dictionary<string, object> body, string key, Vector3 fallback)
        {
            if (!body.TryGetValue(key, out var raw) || !(raw is Dictionary<string, object> v)) return fallback;
            float x = v.TryGetValue("x", out var xv) && xv != null ? (float)JsonNum.ToDouble(xv) : fallback.x;
            float y = v.TryGetValue("y", out var yv) && yv != null ? (float)JsonNum.ToDouble(yv) : fallback.y;
            float z = v.TryGetValue("z", out var zv) && zv != null ? (float)JsonNum.ToDouble(zv) : fallback.z;
            return new Vector3(x, y, z);
        }

        // Per-component-fallback idiom, extended to w — a partial quaternion
        // (e.g. only x/y supplied) fills its remaining components from the
        // fallback (identity by default) rather than rejecting the request;
        // /act/transform/set is the endpoint that enforces strict
        // all-or-nothing validation on a supplied rotation, not
        // create/instantiate.
        internal static Quaternion ReadQuaternion(Dictionary<string, object> body, string key, Quaternion fallback)
        {
            if (!body.TryGetValue(key, out var raw) || !(raw is Dictionary<string, object> v)) return fallback;
            float x = v.TryGetValue("x", out var xv) && xv != null ? (float)JsonNum.ToDouble(xv) : fallback.x;
            float y = v.TryGetValue("y", out var yv) && yv != null ? (float)JsonNum.ToDouble(yv) : fallback.y;
            float z = v.TryGetValue("z", out var zv) && zv != null ? (float)JsonNum.ToDouble(zv) : fallback.z;
            float w = v.TryGetValue("w", out var wv) && wv != null ? (float)JsonNum.ToDouble(wv) : fallback.w;
            return new Quaternion(x, y, z, w);
        }
    }
}
