using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UnityBridge.Editor
{
    internal static class AssetEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "GET",
                Path = "/asset/{guid}",
                TopicKey = "asset",
                Tier = "indexed",
                Summary = "Detail for one asset by GUID, incl. components",
                Params = new[] { "guid (string, required in URL path): asset GUID, from /query" },
                ExampleRequest = "GET /asset/<guid>",
                ExampleResponseAbbrev = "{\"tier\":\"indexed\",\"indexedAt\":\"...\",\"guid\":\"...\",\"path\":\"Assets/...\",\"type\":\"prefab\",\"name\":\"TestAudioCube\",\"components\":[{\"guid\":\"...\",\"componentType\":\"AudioSource\",\"objectPath\":\"TestAudioCube\"}]}",
                TimeoutMs = 1000,
                IsTopicRoute = true,
                ParamPrefix = "/asset/",
                Handler = Handle
            });
        }

        static Dictionary<string, object> Handle(BridgeRequestContext ctx)
        {
            string guid = ctx.Topic;
            if (string.IsNullOrEmpty(guid))
                throw new BridgeHttpException(400, "missing guid — GET /asset/{guid}");

            var asset = IndexStore.GetAsset(guid);
            if (asset == null)
                throw new BridgeHttpException(404, "asset not found — guid unknown or asset deleted; re-query, don't retry");

            var body = asset.ToDict();
            body["tier"] = "indexed";
            body["indexedAt"] = IndexStore.LastUpdatedIso;

            if (asset.Type == "prefab")
            {
                var components = IndexStore.GetPrefabComponents(guid);
                body["components"] = components.Select(c => (object)c.ToDict()).ToList();
                ResponseCapping.ApplyListCap(body, "components", components.Count,
                    "large component list — narrow with /query {\"hasComponent\":...} instead of enumerating");
            }
            else if (asset.Type == "scene")
            {
                // Scene detail exposes native scene_components and the raw
                // prefab-instance list separately rather than pre-flattening
                // them into one merged tree — /query's hasComponent already
                // performs the join for search; this stays closer to the
                // underlying jsonl shape for a caller that wants to expand
                // an instance's own prefab via a follow-up /asset/{guid}.
                var instances = IndexStore.GetScenePrefabInstances(guid);
                var instanceList = instances.Select(p => (object)p.ToDict()).ToList();
                body["prefabInstances"] = instanceList;

                var components = IndexStore.GetSceneComponents(guid);
                body["components"] = components.Select(c => (object)c.ToDict()).ToList();
                ResponseCapping.ApplyListCap(body, "components", components.Count,
                    "large component list — narrow with /query {\"hasComponent\":...} instead of enumerating");

                // ApplyListCap only shrinks "components" (the LOCKED
                // truncated/total/hint trio is meaningless split across two
                // lists in one response). If prefabInstances alone still
                // pushes the body over the cap — unusual, a scene would need
                // an enormous instance count — fall back to trimming it too,
                // best-effort, without separate truncation metadata.
                while (instanceList.Count > 0 && Encoding.UTF8.GetByteCount(MiniJson.Write(body)) > 16384)
                    instanceList.RemoveAt(instanceList.Count - 1);
            }

            return body;
        }
    }
}
