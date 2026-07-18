using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;

namespace UnityBridge.Editor
{
    // POST /act/playmode/enter, POST /act/playmode/exit (v1.5 LOCKED).
    // Accepted-then-reconnect: entering/exiting play mode typically
    // triggers a domain reload, which tears down the listener — so the
    // response is sent BEFORE the state change runs. BuildEnter/BuildExit
    // only check cached, thread-safe state (BridgeState.CachedReadyState)
    // and never call the live Unity API directly; the actual
    // EditorApplication.isPlaying toggle is deferred to ActionScheduler,
    // which runs it on the next Tick() (main thread).
    internal static class ActPlaymodeEndpoint
    {
        // Cooldown added 2026-07-18 after a real Editor crash during
        // testing (see package README DECISIONS for the full incident and
        // investigation). NOT a confirmed fix — the same crash signature
        // (DXGI_ERROR_DEVICE_REMOVED, a GPU driver device-removal event)
        // recurred in two earlier, unrelated crashes days before this
        // endpoint existed, pointing at a standing GPU driver issue on
        // that machine rather than something uniquely caused by rapid
        // playmode toggling. Kept anyway as a cheap, harmless precaution
        // against one plausible contributing factor.
        //
        // Uses a plain Stopwatch, not EditorApplication.timeSinceStartup —
        // this runs on the request thread (inside ActionScheduler's lock),
        // and the codebase's standing rule is that only pre-cached,
        // thread-safe state gets read off the main thread, never the live
        // Unity API. A Stopwatch has no such restriction.
        static readonly Stopwatch _cooldownClock = Stopwatch.StartNew();
        static long _lastAcceptedToggleMs = long.MinValue / 2; // "never" — first request always passes
        const long CooldownMs = 1000;

        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "POST",
                Path = "/act/playmode/enter",
                TopicKey = "act-playmode-enter",
                Tier = "act",
                Summary = "Enter play mode — accepted, then reconnect",
                Params = System.Array.Empty<string>(),
                ExampleRequest = "POST /act/playmode/enter (header X-Bridge-Token: <token>)",
                ExampleResponseAbbrev = "{\"tier\":\"act\",\"accepted\":true,\"willReload\":true}",
                TimeoutMs = 5000,
                BuildAct = BuildEnter
            });

            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "POST",
                Path = "/act/playmode/exit",
                TopicKey = "act-playmode-exit",
                Tier = "act",
                Summary = "Exit play mode — accepted, then reconnect",
                Params = System.Array.Empty<string>(),
                ExampleRequest = "POST /act/playmode/exit (header X-Bridge-Token: <token>)",
                ExampleResponseAbbrev = "{\"tier\":\"act\",\"accepted\":true,\"willReload\":true}",
                TimeoutMs = 5000,
                BuildAct = BuildExit
            });
        }

        static ActBuildResult BuildEnter()
        {
            if (TryCooldownReject(out var cooldownResult)) return cooldownResult;

            if (BridgeState.CachedReadyState == "playmode")
            {
                return new ActBuildResult(409, new Dictionary<string, object>
                {
                    { "tier", "act" }, { "error", "already_in_state" }, { "current", BridgeState.CachedReadyState }
                });
            }

            var body = new Dictionary<string, object>
            {
                { "tier", "act" }, { "accepted", true }, { "willReload", BridgeState.CachedWillReloadOnPlaymodeToggle }
            };
            _lastAcceptedToggleMs = _cooldownClock.ElapsedMilliseconds;
            return new ActBuildResult(202, body, () => EditorApplication.isPlaying = true);
        }

        static ActBuildResult BuildExit()
        {
            if (TryCooldownReject(out var cooldownResult)) return cooldownResult;

            if (BridgeState.CachedReadyState != "playmode")
            {
                return new ActBuildResult(409, new Dictionary<string, object>
                {
                    { "tier", "act" }, { "error", "already_in_state" }, { "current", BridgeState.CachedReadyState }
                });
            }

            var body = new Dictionary<string, object>
            {
                { "tier", "act" }, { "accepted", true }, { "willReload", BridgeState.CachedWillReloadOnPlaymodeToggle }
            };
            _lastAcceptedToggleMs = _cooldownClock.ElapsedMilliseconds;
            return new ActBuildResult(202, body, () => EditorApplication.isPlaying = false);
        }

        // Shared across enter and exit — at most one accepted toggle per
        // CooldownMs, regardless of direction (LOCKED, v1.5 brief).
        // Checked before idempotence: a rate-limit rejection shouldn't
        // depend on which specific state we're currently in.
        static bool TryCooldownReject(out ActBuildResult result)
        {
            long elapsedSinceLastAccepted = _cooldownClock.ElapsedMilliseconds - _lastAcceptedToggleMs;
            if (elapsedSinceLastAccepted < CooldownMs)
            {
                result = new ActBuildResult(429, new Dictionary<string, object>
                {
                    { "tier", "act" }, { "error", "cooldown" }, { "retryAfterMs", CooldownMs - elapsedSinceLastAccepted }
                });
                return true;
            }
            result = default;
            return false;
        }
    }
}
