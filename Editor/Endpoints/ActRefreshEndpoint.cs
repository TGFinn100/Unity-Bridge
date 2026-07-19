using System.Collections.Generic;
using UnityEditor;

namespace UnityBridge.Editor
{
    // POST /act/refresh (v1.5 LOCKED). Calls AssetDatabase.Refresh() so
    // Claude can trigger the import of files it just wrote instead of
    // waiting for Unity's focus-based auto-refresh. Same accepted-then-
    // reconnect shape as playmode enter/exit.
    internal static class ActRefreshEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "POST",
                Path = "/act/refresh",
                TopicKey = "act-refresh",
                Tier = "act",
                Summary = "AssetDatabase.Refresh() — import written files",
                Params = System.Array.Empty<string>(),
                ExampleRequest = "POST /act/refresh (header X-Bridge-Token: <token>)",
                ExampleResponseAbbrev = "{\"tier\":\"act\",\"accepted\":true,\"willReload\":true}",
                TimeoutMs = 5000,
                BuildAct = Build
            });
        }

        // No idempotence rule (LOCKED, v1.5 brief): AssetDatabase.Refresh()
        // is safe to call repeatedly, unlike playmode's stateful toggle.
        // willReload is a deliberate over-approximation (always true)
        // rather than a precise pending-script-changes check — the brief
        // explicitly allows "detection may be approximate; the reconnect
        // contract makes precision unnecessary." A false-positive
        // willReload costs nothing since the client already treats any
        // post-/act refusal as a healthy reload.
        // Unused param — see ActPlaymodeEndpoint.BuildEnter's comment.
        static ActBuildResult Build(Dictionary<string, object> _)
        {
            var body = new Dictionary<string, object> { { "tier", "act" }, { "accepted", true }, { "willReload", true } };
            return new ActBuildResult(202, body, () => AssetDatabase.Refresh());
        }
    }
}
