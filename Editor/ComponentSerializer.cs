using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UnityBridge.Editor
{
    // Component name/value list serialization, extracted from ObjectEndpoint
    // (v2) so MutationNodeBuilder's single-node mutation responses can reuse
    // the exact same shape instead of duplicating it.
    internal static class ComponentSerializer
    {
        internal static List<object> ComponentNames(GameObject go) =>
            go.GetComponents<Component>()
                .Select(c => (object)(c == null ? "<Missing Script>" : c.GetType().Name))
                .ToList();

        internal static List<object> ComponentValues(GameObject go)
        {
            var list = new List<object>();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                list.Add(new Dictionary<string, object>
                {
                    { "type", c.GetType().Name },
                    { "fields", SerializedValueExtractor.ExtractFields(c) }
                });
            }
            return list;
        }
    }
}
