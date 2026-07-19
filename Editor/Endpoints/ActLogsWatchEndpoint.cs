using System;
using System.Collections.Generic;

namespace UnityBridge.Editor
{
    // POST /act/logs/watch (v1.6 LOCKED). Registers a named, condensed log
    // watch. Validation (missing fields, duplicate name, invalid regex) runs
    // inline in BuildAct against cached/thread-safe state — same discipline
    // as ActPlaymodeEndpoint's idempotence check — so a rejected request
    // never schedules a main-thread action. The actual mutation
    // (LogWatchStore.Register, which writes the manifest + an empty records
    // file) is deferred to the next Tick() via MainThreadAction, matching
    // every other Library/UnityBridge/ writer in this codebase.
    internal static class ActLogsWatchEndpoint
    {
        const int DefaultCapacity = 500; // LOCKED, v1.6 brief "Retention cap"

        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "POST",
                Path = "/act/logs/watch",
                TopicKey = "act-logs-watch",
                Tier = "act",
                Summary = "Register a named condensed log watch",
                Params = new[]
                {
                    "name (string, required): unique watch name",
                    "pattern (string, required): .NET regex with named capture groups, e.g. \"Health: (?<value>\\d+)\"",
                    "capacity (int, optional): per-watch ring-buffer cap, default 500"
                },
                ExampleRequest = "POST /act/logs/watch {\"name\":\"health\",\"pattern\":\"Health: (?<value>\\\\d+)\"} (header X-Bridge-Token: <token>)",
                ExampleResponseAbbrev = "{\"tier\":\"act\",\"accepted\":true,\"willReload\":false,\"watch\":{\"name\":\"health\",\"pattern\":\"Health: (?<value>\\\\d+)\",\"capacity\":500,\"createdAt\":\"2026-07-19T12:00:00.000Z\"}}",
                TimeoutMs = 5000,
                BuildAct = Build
            });
        }

        static ActBuildResult Build(Dictionary<string, object> body)
        {
            body = body ?? new Dictionary<string, object>();

            string name = GetString(body, "name");
            if (string.IsNullOrWhiteSpace(name))
                return new ActBuildResult(400, Err("invalid_request", "name is required"));

            string pattern = GetString(body, "pattern");
            if (string.IsNullOrWhiteSpace(pattern))
                return new ActBuildResult(400, Err("invalid_request", "pattern is required"));

            int capacity = DefaultCapacity;
            if (body.TryGetValue("capacity", out var rawCapacity) && rawCapacity != null)
            {
                capacity = (int)JsonNum.ToLong(rawCapacity);
                if (capacity <= 0)
                    return new ActBuildResult(400, Err("invalid_request", "capacity must be a positive integer"));
            }

            // LOCKED failure mode: duplicate name -> 409, no action.
            if (LogWatchStore.Exists(name))
            {
                var conflict = new Dictionary<string, object> { { "tier", "act" }, { "error", "already_watching" }, { "name", name } };
                return new ActBuildResult(409, conflict);
            }

            // LOCKED failure mode: invalid regex -> 400, validated now so a
            // bad pattern never silently matches nothing forever.
            try
            {
                LogWatchStore.CompileValidate(pattern);
            }
            catch (Exception ex)
            {
                var invalid = new Dictionary<string, object> { { "tier", "act" }, { "error", "invalid_pattern" }, { "detail", ex.Message } };
                return new ActBuildResult(400, invalid);
            }

            string createdAt = DateTime.UtcNow.ToString("o");
            var watchDict = new Dictionary<string, object>
            {
                { "name", name }, { "pattern", pattern }, { "capacity", capacity }, { "createdAt", createdAt }
            };

            // No reload follows a watch registration (LOCKED — unlike
            // playmode/refresh, this never tears down the listener), so
            // willReload is always false here rather than the "accepted-
            // then-reconnect" true used by the other /act endpoints.
            var responseBody = new Dictionary<string, object>
            {
                { "tier", "act" }, { "accepted", true }, { "willReload", false }, { "watch", watchDict }
            };
            return new ActBuildResult(202, responseBody, () => LogWatchStore.Register(name, pattern, capacity, createdAt));
        }

        static string GetString(Dictionary<string, object> body, string key) =>
            body.TryGetValue(key, out var v) ? v as string : null;

        static Dictionary<string, object> Err(string error, string detail) =>
            new Dictionary<string, object> { { "tier", "act" }, { "error", error }, { "detail", detail } };
    }
}
