using System.Collections.Generic;
using System.Linq;

namespace UnityBridge.Editor
{
    // GET /logs/watches (v1.6 LOCKED). Meta tier, same reasoning as
    // /ping and /help: answers directly from in-memory state (loaded from
    // the on-disk manifest at startup, kept current by
    // ActLogsWatchEndpoint/ActLogsUnwatchEndpoint), no main-thread Unity API
    // or index-store dependency needed. The sanctioned discovery path — an
    // agent does not read log_watches/manifest.json directly.
    internal static class LogsWatchesEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "GET",
                Path = "/logs/watches",
                TopicKey = "logs-watches",
                Tier = "meta",
                Summary = "List active named log watches",
                Params = System.Array.Empty<string>(),
                ExampleRequest = "GET /logs/watches",
                ExampleResponseAbbrev = "{\"tier\":\"meta\",\"watches\":[{\"name\":\"health\",\"pattern\":\"Health: (?<value>\\\\d+)\",\"capacity\":500,\"createdAt\":\"2026-07-19T12:00:00.000Z\"}]}",
                TimeoutMs = 5000,
                Handler = Handle
            });
        }

        static Dictionary<string, object> Handle(BridgeRequestContext ctx)
        {
            var watches = LogWatchStore.ListActive().Select(d => (object)d.ToDict()).ToList();
            return new Dictionary<string, object> { { "tier", "meta" }, { "watches", watches } };
        }
    }
}
