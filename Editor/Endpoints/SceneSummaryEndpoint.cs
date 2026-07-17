using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityBridge.Editor
{
    internal static class SceneSummaryEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "GET",
                Path = "/scene/summary",
                TopicKey = "scene-summary",
                Tier = "live",
                Summary = "Open scene(s): top-level roots, never full hierarchy",
                Params = System.Array.Empty<string>(),
                ExampleRequest = "GET /scene/summary",
                ExampleResponseAbbrev = "{\"tier\":\"live\",\"scenes\":[{\"name\":\"SampleScene\",\"path\":\"Assets/Scenes/SampleScene.unity\",\"rootCount\":2}],\"roots\":[{\"id\":\"GlobalObjectId_V1-2-...\",\"name\":\"Main Camera\",\"scene\":\"SampleScene\",\"childCount\":0,\"components\":[\"Transform\",\"Camera\"]}],\"truncated\":false,\"total\":2}",
                TimeoutMs = 5000,
                Handler = Handle
            });
        }

        static Dictionary<string, object> Handle(BridgeRequestContext ctx)
        {
            var scenesSummary = new List<object>();
            var roots = new List<object>();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                var rootObjects = scene.GetRootGameObjects();
                scenesSummary.Add(new Dictionary<string, object>
                {
                    { "name", scene.name },
                    { "path", scene.path },
                    { "rootCount", rootObjects.Length }
                });

                foreach (var go in rootObjects)
                {
                    roots.Add(new Dictionary<string, object>
                    {
                        { "id", GlobalObjectId.GetGlobalObjectIdSlow(go).ToString() },
                        { "name", go.name },
                        { "scene", scene.name },
                        { "childCount", go.transform.childCount },
                        { "components", ComponentNames(go) }
                    });
                }
            }

            int total = roots.Count;
            var body = new Dictionary<string, object>
            {
                { "tier", "live" },
                { "scenes", scenesSummary },
                { "roots", roots }
            };

            ResponseCapping.ApplyListCap(body, "roots", total,
                "narrow by drilling into a specific object via /object/{id} instead of listing every root");
            BridgeState.AddFrameIfPlaying(body);
            return body;
        }

        static List<object> ComponentNames(GameObject go) =>
            go.GetComponents<Component>()
                .Select(c => (object)(c == null ? "<Missing Script>" : c.GetType().Name))
                .ToList();
    }
}
