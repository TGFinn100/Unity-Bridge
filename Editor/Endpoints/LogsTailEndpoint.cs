using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityBridge.Editor
{
    internal static class LogsTailEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "GET",
                Path = "/logs/tail",
                TopicKey = "logs-tail",
                Tier = "live",
                Summary = "Recent console entries, newest first",
                Params = new[]
                {
                    "n (int, optional): default 20, max 100",
                    "severity (string, optional): \"error\" | \"warn\" | \"all\" (default \"all\")"
                },
                ExampleRequest = "GET /logs/tail?n=20&severity=error",
                ExampleResponseAbbrev = "{\"tier\":\"live\",\"entries\":[{\"severity\":\"error\",\"message\":\"bridge test\",\"stackFrame\":\"MyScript.Start () (at Assets/MyScript.cs:5)\",\"time\":\"2026-07-17T12:00:00.000Z\"}],\"truncated\":false,\"total\":1}",
                TimeoutMs = 5000,
                Handler = Handle
            });
        }

        static Dictionary<string, object> Handle(BridgeRequestContext ctx)
        {
            int n = GetN(ctx.Query);
            string severity = GetSeverity(ctx.Query);

            var all = LogBuffer.Snapshot(); // oldest-first
            var matching = severity == "all" ? all : all.Where(e => e.Severity == severity).ToList();
            int total = matching.Count;

            // Newest-first per DECISIONS (v1 ordering choice, not LOCKED):
            // an agent tailing logs right after making a change wants the
            // most recent entry first, not buried at the bottom.
            var entries = matching.AsEnumerable().Reverse().Take(n).Select(e => (object)e.ToDict()).ToList();

            var body = new Dictionary<string, object>
            {
                { "tier", "live" },
                { "entries", entries }
            };

            ResponseCapping.ApplyListCap(body, "entries", total, "narrow with severity=error/warn or reduce n");
            BridgeState.AddFrameIfPlaying(body);
            return body;
        }

        static int GetN(IReadOnlyDictionary<string, string> query)
        {
            int n = 20;
            if (query.TryGetValue("n", out var raw) && int.TryParse(raw, out int parsed)) n = parsed;
            return Math.Max(1, Math.Min(n, 100));
        }

        static string GetSeverity(IReadOnlyDictionary<string, string> query)
        {
            if (!query.TryGetValue("severity", out var raw) || raw == "all") return "all";
            if (raw == "error" || raw == "warn") return raw;
            throw new BridgeHttpException(400, "severity must be \"error\", \"warn\", or \"all\"");
        }
    }
}
