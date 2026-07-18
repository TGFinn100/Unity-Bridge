using System.Collections.Generic;
using System.Linq;

namespace UnityBridge.Editor
{
    internal static class QueryEndpoint
    {
        static readonly string[] ValidFields = { "guid", "path", "type", "name", "sizeBytes", "mtime" };
        static readonly string[] DefaultFields = { "guid", "path", "type", "name" };

        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "POST",
                Path = "/query",
                TopicKey = "query",
                Tier = "indexed",
                Summary = "Structured filters over the project index",
                Params = new[]
                {
                    "type (string, optional): friendly asset type, e.g. \"prefab\", \"scene\", \"script\"",
                    "nameGlob (string, optional): * and ? glob matched against asset name",
                    "pathPrefix (string, optional): prefix match against asset path",
                    "hasComponent (string, optional): component type name; joins prefab/scene component tables, including scenes via prefab instances. Caveat: override-REMOVED components on prefab instances are not resolved — results may rarely include a component an instance has actually removed (benign false positive). Confirm with a live /object call when precision matters.",
                    "fields (string[], optional): subset of guid|path|type|name|sizeBytes|mtime, defaults to guid,path,type,name",
                    "limit (int, optional): default 20, max 100"
                },
                ExampleRequest = "POST /query {\"hasComponent\":\"AudioSource\"}",
                ExampleResponseAbbrev = "{\"tier\":\"indexed\",\"indexedAt\":\"...\",\"results\":[{\"guid\":\"...\",\"path\":\"Assets/...\",\"type\":\"prefab\",\"name\":\"TestAudioCube\"}],\"total\":1,\"truncated\":false}",
                TimeoutMs = 1000,
                Handler = Handle
            });
        }

        static Dictionary<string, object> Handle(BridgeRequestContext ctx)
        {
            var body = ctx.Body ?? new Dictionary<string, object>();

            var filter = new QueryFilter
            {
                Type = GetString(body, "type"),
                NameGlob = GetString(body, "nameGlob"),
                PathPrefix = GetString(body, "pathPrefix"),
                HasComponent = GetString(body, "hasComponent"),
                Limit = body.TryGetValue("limit", out var limitVal) ? (int)JsonNum.ToLong(limitVal) : 20
            };

            string[] fields = ResolveFields(body);

            var (items, total) = IndexStore.Query(filter);

            var results = new List<object>();
            foreach (var item in items)
            {
                var full = item.ToDict();
                var projected = new Dictionary<string, object>();
                foreach (var f in fields)
                    if (full.TryGetValue(f, out var v)) projected[f] = v;
                results.Add(projected);
            }

            var responseBody = new Dictionary<string, object>
            {
                { "tier", "indexed" },
                { "indexedAt", IndexStore.LastUpdatedIso },
                { "results", results }
            };

            ResponseCapping.ApplyListCap(responseBody, "results", total,
                "narrow with pathPrefix, nameGlob, or hasComponent, or reduce limit/fields");

            return responseBody;
        }

        static string[] ResolveFields(Dictionary<string, object> body)
        {
            if (!body.TryGetValue("fields", out var fieldsVal) || !(fieldsVal is List<object> fieldsList))
                return DefaultFields;

            var requested = fieldsList.Select(f => f?.ToString()).ToArray();
            var unknown = requested.Where(f => !ValidFields.Contains(f)).ToList();
            if (unknown.Count > 0)
            {
                throw new BridgeHttpException(400,
                    $"unknown field(s): {string.Join(", ", unknown)} — valid fields: {string.Join(", ", ValidFields)}");
            }
            return requested;
        }

        static string GetString(Dictionary<string, object> body, string key) =>
            body.TryGetValue(key, out var v) ? v as string : null;
    }
}
