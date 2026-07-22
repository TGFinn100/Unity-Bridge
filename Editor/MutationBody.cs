using System.Collections.Generic;
using UnityEngine;

namespace UnityBridge.Editor
{
    // v2: small shared readers for the 8 mutation endpoints' POST bodies —
    // same spirit as ActLogsWatchEndpoint's own GetString helper (v1.6),
    // pulled out here since every one of the 8 endpoints needs at least one
    // of these.
    internal static class MutationBody
    {
        internal static string GetString(Dictionary<string, object> body, string key) =>
            body != null && body.TryGetValue(key, out var v) ? v as string : null;

        internal static bool GetBool(Dictionary<string, object> body, string key, bool fallback)
        {
            if (body == null || !body.TryGetValue(key, out var v) || !(v is bool b)) return fallback;
            return b;
        }

        // "id" is required on every endpoint that takes one; a missing/empty
        // value folds into GameObjectResolver's own invalid-id 400 (LOCKED
        // "reuse verbatim" resolution chain) rather than a separate
        // missing-field error — GameObjectResolver.ResolveOrThrow already
        // treats null/empty as invalid.
        internal static GameObject ResolveIdOrThrow(Dictionary<string, object> body) =>
            GameObjectResolver.ResolveOrThrow(GetString(body, "id"));

        // Distinguishes an omitted "parent" key (fallback) from an
        // explicitly-present one, since id|null and "not sent at all" mean
        // different things across create/duplicate/reparent (see each
        // endpoint's own default). Returns fallback when the key is absent;
        // otherwise resolves a non-null value or returns null for an
        // explicit JSON null.
        internal static Transform ResolveOptionalParent(Dictionary<string, object> body, Transform fallback)
        {
            if (body == null || !body.ContainsKey("parent")) return fallback;
            var raw = body["parent"];
            return raw == null ? null : GameObjectResolver.ResolveOrThrow(raw as string).transform;
        }
    }
}
