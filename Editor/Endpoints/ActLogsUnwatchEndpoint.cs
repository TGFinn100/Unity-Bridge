using System.Collections.Generic;

namespace UnityBridge.Editor
{
    // POST /act/logs/unwatch (v1.6 LOCKED). Removes a named log watch —
    // deletes the manifest entry and that watch's <name>.jsonl (LOCKED: "no
    // discovery path remains to an orphaned file, so nothing is served by
    // keeping it"). Same deferred-mutation discipline as ActLogsWatchEndpoint.
    internal static class ActLogsUnwatchEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "POST",
                Path = "/act/logs/unwatch",
                TopicKey = "act-logs-unwatch",
                Tier = "act",
                Summary = "Remove a named log watch",
                Params = new[] { "name (string, required): watch name, from GET /logs/watches" },
                ExampleRequest = "POST /act/logs/unwatch {\"name\":\"health\"} (header X-Bridge-Token: <token>)",
                ExampleResponseAbbrev = "{\"tier\":\"act\",\"accepted\":true,\"willReload\":false,\"name\":\"health\"}",
                TimeoutMs = 5000,
                BuildAct = Build
            });
        }

        static ActBuildResult Build(Dictionary<string, object> body)
        {
            body = body ?? new Dictionary<string, object>();

            string name = body.TryGetValue("name", out var v) ? v as string : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                var invalid = new Dictionary<string, object> { { "tier", "act" }, { "error", "invalid_request" }, { "detail", "name is required" } };
                return new ActBuildResult(400, invalid);
            }

            // LOCKED failure mode: unknown name -> 404, no action.
            if (!LogWatchStore.Exists(name))
            {
                var notFound = new Dictionary<string, object> { { "tier", "act" }, { "error", "not_watching" }, { "name", name } };
                return new ActBuildResult(404, notFound);
            }

            var responseBody = new Dictionary<string, object>
            {
                { "tier", "act" }, { "accepted", true }, { "willReload", false }, { "name", name }
            };
            return new ActBuildResult(202, responseBody, () => LogWatchStore.Unregister(name));
        }
    }
}
