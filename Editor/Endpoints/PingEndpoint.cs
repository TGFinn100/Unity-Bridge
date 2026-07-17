using System;
using System.Collections.Generic;

namespace UnityBridge.Editor
{
    internal static class PingEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "GET",
                Path = "/ping",
                TopicKey = "ping",
                Tier = "meta",
                Summary = "Editor readiness, version, project name",
                Params = Array.Empty<string>(),
                ExampleRequest = "GET /ping",
                ExampleResponseAbbrev = "{\"tier\":\"meta\",\"readyState\":\"ready\",\"unityVersion\":\"6000.5.2f1\",\"projectName\":\"Unity MCP\",\"boundPort\":17870}",
                TimeoutMs = 5000,
                Handler = Handle
            });
        }

        static Dictionary<string, object> Handle(BridgeRequestContext ctx)
        {
            return new Dictionary<string, object>
            {
                { "tier", "meta" },
                { "readyState", BridgeState.CachedReadyState },
                { "unityVersion", BridgeState.CachedUnityVersion },
                { "projectName", BridgeState.CachedProjectName },
                { "boundPort", BridgeServer.BoundPort }
            };
        }
    }
}
