using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityBridge.Editor
{
    internal static class ObjectEndpoint
    {
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

            // v2: resolution chain factored out to GameObjectResolver so the
            // v2 mutation endpoints can reuse it verbatim (LOCKED) — same
            // messages, same EntityId-based pair (6000.5.2f1 hard-deprecates
            // the InstanceID-based overloads, error CS0619).
            var go = GameObjectResolver.ResolveOrThrow(ctx.Topic);

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
                { "componentNames", ComponentSerializer.ComponentNames(go) }
            };

            if (GameObjectResolver.IsVolatile(gid)) node["volatile"] = true;

            if (includeValues) node["components"] = ComponentSerializer.ComponentValues(go);

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
