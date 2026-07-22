using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityBridge.Editor
{
    // v2 (LOCKED, task brief "/act/component/add"): resolves a short
    // component type name (e.g. "Rigidbody", "AudioSource") to a Type by
    // searching every loaded assembly for a Component-derived type whose
    // Name matches, case-sensitive — same casing convention as /query's
    // hasComponent filter (StringComparison.Ordinal). Shared by
    // /act/component/add, /act/component/remove, and /act/component/set-field
    // (all three take a "component"/"type" name and need the same lookup).
    internal static class ComponentTypeResolver
    {
        internal static Type ResolveOrThrow(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                throw new MutationRejection(400, new Dictionary<string, object>
                {
                    { "tier", "act" }, { "error", "unknown_component_type" }, { "type", typeName }
                });

            var shortNameMatches = new List<Type>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }

                foreach (var t in types)
                {
                    if (t == null || !typeof(Component).IsAssignableFrom(t)) continue;

                    // A fully-qualified name is exact and can't be
                    // ambiguous — checked first so the brief's own escape
                    // hatch ("caller must retry with a fully-qualified
                    // name" after a 400 ambiguous_type) actually resolves
                    // on retry, instead of hitting the same ambiguity again
                    // via short-name matching below.
                    if (t.FullName == typeName) return t;

                    if (t.Name == typeName) shortNameMatches.Add(t);
                }
            }

            var matches = shortNameMatches;
            if (matches.Count == 0)
            {
                throw new MutationRejection(400, new Dictionary<string, object>
                {
                    { "tier", "act" }, { "error", "unknown_component_type" }, { "type", typeName }
                });
            }

            if (matches.Count > 1)
            {
                var candidates = new List<object>();
                foreach (var t in matches) candidates.Add(t.FullName);
                throw new MutationRejection(400, new Dictionary<string, object>
                {
                    { "tier", "act" }, { "error", "ambiguous_type" }, { "type", typeName }, { "candidates", candidates }
                });
            }

            return matches[0];
        }
    }
}
