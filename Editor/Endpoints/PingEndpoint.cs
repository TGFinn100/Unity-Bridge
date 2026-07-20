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
                ExampleResponseAbbrev = "{\"tier\":\"meta\",\"readyState\":\"ready\",\"unityVersion\":\"6000.5.2f1\",\"projectName\":\"Unity MCP\",\"boundPort\":17870,\"indexedAt\":\"2026-07-17T12:00:00.000Z\",\"schemaVersion\":1,\"compileErrorCount\":0,\"compileWarningCount\":0,\"compileMessages\":[],\"compileMessagesTruncated\":false,\"compileMessagesTotal\":0}",
                TimeoutMs = 5000,
                Handler = Handle
            });
        }

        static Dictionary<string, object> Handle(BridgeRequestContext ctx)
        {
            var messages = BridgeState.CompileMessagesAsObjectList();
            int total = BridgeState.CachedCompileErrorCount + BridgeState.CachedCompileWarningCount;
            bool truncated = messages.Count < total;

            var body = new Dictionary<string, object>
            {
                { "tier", "meta" },
                { "readyState", BridgeState.CachedReadyState },
                { "unityVersion", BridgeState.CachedUnityVersion },
                { "projectName", BridgeState.CachedProjectName },
                { "boundPort", BridgeServer.BoundPort },
                { "indexedAt", IndexStore.LastUpdatedIso },
                { "schemaVersion", IndexStore.SchemaVersion },
                { "compileErrorCount", BridgeState.CachedCompileErrorCount },
                { "compileWarningCount", BridgeState.CachedCompileWarningCount },
                { "compileMessages", messages },
                { "compileMessagesTruncated", truncated },
                { "compileMessagesTotal", total }
            };
            // Deliberately separate from the truncated/total/hint trio's
            // usual "hint" key name — /ping isn't a list-response endpoint
            // in that existing sense (LOCKED, task brief).
            if (truncated) body["compileMessagesHint"] = "read Editor.log directly for full output";
            return body;
        }
    }
}
