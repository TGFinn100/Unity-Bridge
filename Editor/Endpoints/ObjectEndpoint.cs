using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityBridge.Editor
{
    internal static class ObjectEndpoint
    {
        static readonly string ZeroGuid = new string('0', 32);

        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "GET",
                Path = "/object/{id}",
                TopicKey = "object",
                Tier = "live",
                Summary = "One scene object by GlobalObjectId, bounded depth",
                Params = new[]
                {
                    "id (string, required in URL path): GlobalObjectId, from /scene/summary or a parent /object/{id}",
                    "depth (int, optional): 0-2, default 0. 0 = object only, 1 = + direct children, 2 = + grandchildren",
                    "components (string, optional): \"names\" (default) or \"values\" — values expands serialized fields for the root object's own components only, never for children"
                },
                ExampleRequest = "GET /object/<id>?depth=1&components=names",
                ExampleResponseAbbrev = "{\"tier\":\"live\",\"id\":\"GlobalObjectId_V1-2-...\",\"name\":\"Cube\",\"active\":true,\"componentNames\":[\"Transform\",\"AudioSource\"],\"children\":[{\"id\":\"...\",\"name\":\"Child\",\"active\":true,\"componentNames\":[\"Transform\"]}]}",
                TimeoutMs = 5000,
                IsTopicRoute = true,
                ParamPrefix = "/object/",
                Handler = Handle
            });
        }

        static Dictionary<string, object> Handle(BridgeRequestContext ctx)
        {
            if (string.IsNullOrEmpty(ctx.Topic))
                throw new BridgeHttpException(400, "missing id — GET /object/{id}");

            if (!GlobalObjectId.TryParse(ctx.Topic, out GlobalObjectId gid))
                throw new BridgeHttpException(400, "invalid id format — expected a GlobalObjectId string from /scene/summary or /object/{id}");

            // 6000.5.2f1 hard-deprecates the InstanceID-based overloads
            // (error CS0619, not just a warning) in favor of the EntityId
            // ones — same "resolve to a live object or get nothing back"
            // contract, just renamed. No separate zero/invalid check
            // needed: an unresolvable id flows straight through to the
            // null check below, same as the old instanceId==0 case did.
            var entityId = GlobalObjectId.GlobalObjectIdentifierToEntityIdSlow(gid);
            var go = EditorUtility.EntityIdToObject(entityId) as GameObject;
            if (go == null)
                throw new BridgeHttpException(404, "stale id — object gone or state reloaded; re-discover, don't retry");

            int depth = GetDepth(ctx.Query);
            bool includeValues = GetIncludeValues(ctx.Query);

            var body = BuildNode(go, depth, includeValues);
            body["tier"] = "live";

            int totalNodes = CountNodes(body);
            bool truncated = TrimToCap(body);
            body["truncated"] = truncated;
            body["total"] = totalNodes;
            if (truncated) body["hint"] = "large subtree — request a lower depth or drill into a specific child by id";

            BridgeState.AddFrameIfPlaying(body);
            return body;
        }

        static Dictionary<string, object> BuildNode(GameObject go, int remainingDepth, bool includeValues)
        {
            var gid = GlobalObjectId.GetGlobalObjectIdSlow(go);
            var node = new Dictionary<string, object>
            {
                { "id", gid.ToString() },
                { "name", go.name },
                { "active", go.activeSelf },
                { "componentNames", ComponentNames(go) }
            };

            if (IsVolatile(gid)) node["volatile"] = true;

            if (includeValues) node["components"] = ComponentValues(go);

            if (remainingDepth > 0)
            {
                var children = new List<object>();
                var t = go.transform;
                for (int i = 0; i < t.childCount; i++)
                    children.Add(BuildNode(t.GetChild(i).gameObject, remainingDepth - 1, includeValues: false));
                node["children"] = children;
            }

            return node;
        }

        static List<object> ComponentNames(GameObject go) =>
            go.GetComponents<Component>()
                .Select(c => (object)(c == null ? "<Missing Script>" : c.GetType().Name))
                .ToList();

        static List<object> ComponentValues(GameObject go)
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

        // Unity's documented signal for a non-persistent object (never
        // saved to a scene/prefab file, e.g. instantiated at runtime): the
        // GlobalObjectId's assetGUID comes back all-zero. Same encoding
        // also fires for an object in a scene that itself has never been
        // saved — treated as volatile too, which is arguably correct (it
        // doesn't persist anywhere yet either). This is exactly what the
        // task brief's Phase 3 verification task (3.6) asks to be confirmed
        // by hand; log the outcome in the package README's DECISIONS
        // heading either way.
        static bool IsVolatile(GlobalObjectId gid) => gid.assetGUID.ToString() == ZeroGuid;

        static int GetDepth(IReadOnlyDictionary<string, string> query)
        {
            if (!query.TryGetValue("depth", out var raw)) return 0;
            if (!int.TryParse(raw, out int depth) || depth < 0 || depth > 2)
                throw new BridgeHttpException(400, "depth must be 0, 1, or 2");
            return depth;
        }

        static bool GetIncludeValues(IReadOnlyDictionary<string, string> query)
        {
            if (!query.TryGetValue("components", out var raw) || raw == "names") return false;
            if (raw == "values") return true;
            throw new BridgeHttpException(400, "components must be \"names\" or \"values\"");
        }

        static int CountNodes(Dictionary<string, object> node)
        {
            int count = 1;
            if (node.TryGetValue("children", out var childrenObj) && childrenObj is List<object> children)
                foreach (var child in children)
                    if (child is Dictionary<string, object> childDict)
                        count += CountNodes(childDict);
            return count;
        }

        // The shared ResponseCapping helper assumes one flat top-level list
        // (LOCKED truncated/total/hint trio); a depth-2 object tree is
        // nested, so this repeatedly drops the last child of whichever node
        // currently has the most direct children, wherever it sits in the
        // tree, until the whole body fits — same "structural, not
        // byte-level" truncation contract, just walked recursively. Returns
        // whether any trimming actually happened.
        static bool TrimToCap(Dictionary<string, object> body)
        {
            bool trimmedAny = false;
            while (Encoding.UTF8.GetByteCount(MiniJson.Write(body)) > 16384)
            {
                List<object> widest = null;
                FindWidestChildrenList(body, ref widest);
                if (widest == null || widest.Count == 0) break; // nothing left to trim; best-effort from here
                widest.RemoveAt(widest.Count - 1);
                trimmedAny = true;
            }
            return trimmedAny;
        }

        static void FindWidestChildrenList(object node, ref List<object> widest)
        {
            if (!(node is Dictionary<string, object> dict)) return;
            if (dict.TryGetValue("children", out var childrenObj) && childrenObj is List<object> children)
            {
                if (widest == null || children.Count > widest.Count) widest = children;
                foreach (var child in children) FindWidestChildrenList(child, ref widest);
            }
        }
    }
}
