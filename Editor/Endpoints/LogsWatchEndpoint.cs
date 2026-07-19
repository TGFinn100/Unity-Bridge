using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityBridge.Editor
{
    // GET /logs/watch/{name} — not in the v1.6 task brief's original
    // Endpoints table (a real gap: the brief's own skill-file idiom says
    // "... -> GET /logs/watches to confirm it's active -> read back
    // condensed history -> ...", acceptance criterion 1 requires confirming
    // matched messages land in a watch's .jsonl, but no endpoint anywhere in
    // the LOCKED spec actually returns condensed records). Added after
    // flagging the gap to the user (2026-07-19); user chose this resolution
    // (new endpoint, mirroring /asset/{guid} and /object/{id}'s existing
    // param-route pattern) over folding records into GET /logs/watches.
    //
    // Live tier (matches /logs/tail and /playmode, both of which also read
    // BridgeState.AddFrameIfPlaying — reading the frame count needs the
    // main thread, hence "live" rather than "meta").
    internal static class LogsWatchEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "GET",
                Path = "/logs/watch/{name}",
                TopicKey = "logs-watch",
                Tier = "live",
                Summary = "Condensed records for one log watch, newest first",
                Params = new[]
                {
                    "name (string, required in URL path): watch name, from GET /logs/watches",
                    "n (int, optional): default 20, max 100"
                },
                ExampleRequest = "GET /logs/watch/health?n=20",
                ExampleResponseAbbrev = "{\"tier\":\"live\",\"name\":\"health\",\"pattern\":\"Health: (?<value>\\\\d+)\",\"capacity\":500,\"createdAt\":\"2026-07-19T12:00:00.000Z\",\"records\":[{\"values\":{\"value\":\"87\"},\"time\":\"2026-07-19T12:00:01.000Z\"}],\"truncated\":false,\"total\":1}",
                TimeoutMs = 5000,
                IsTopicRoute = true,
                ParamPrefix = "/logs/watch/",
                Handler = Handle
            });
        }

        static Dictionary<string, object> Handle(BridgeRequestContext ctx)
        {
            string name = ctx.Topic;
            if (string.IsNullOrEmpty(name))
                throw new BridgeHttpException(400, "missing name — GET /logs/watch/{name}");

            var (items, total, pattern, capacity, createdAt) = LogWatchStore.GetRecords(name);
            if (items == null)
                throw new BridgeHttpException(404, $"unknown watch \"{name}\" — see /logs/watches");

            int n = GetN(ctx.Query);
            var records = items.Take(n).Select(r => (object)r.ToDict()).ToList();

            var body = new Dictionary<string, object>
            {
                { "tier", "live" },
                { "name", name },
                { "pattern", pattern },
                { "capacity", capacity },
                { "createdAt", createdAt },
                { "records", records }
            };

            ResponseCapping.ApplyListCap(body, "records", total, "reduce n");
            BridgeState.AddFrameIfPlaying(body);
            return body;
        }

        static int GetN(IReadOnlyDictionary<string, string> query)
        {
            int n = 20;
            if (query.TryGetValue("n", out var raw) && int.TryParse(raw, out int parsed)) n = parsed;
            return Math.Max(1, Math.Min(n, 100));
        }
    }
}
