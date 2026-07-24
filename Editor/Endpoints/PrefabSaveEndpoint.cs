using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityBridge.Editor
{
    // POST /act/prefab/save (v2.5 LOCKED). New base prefab only — no Prefab
    // Variant creation here (deferred to a future slice if needed).
    // Self-protection reinstated for this one endpoint (v2 dropped
    // IndexStore.SelfPackagePrefix-style protection generally, noting it
    // should return once asset-level mutation existed — this is that
    // endpoint, the only one in this brief that takes a caller-specified
    // filesystem path).
    internal static class PrefabSaveEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "POST",
                Path = "/act/prefab/save",
                TopicKey = "act-prefab-save",
                Tier = "act",
                Synchronous = true,
                Summary = "Save a GameObject as a new prefab asset",
                Params = new[]
                {
                    "id (string, required): GlobalObjectId of the scene GameObject to save",
                    "path (string, required): new asset path, e.g. \"Assets/Prefabs/Foo.prefab\" — must be under Assets/, outside the bridge's own package, and end in .prefab"
                },
                ExampleRequest = "POST /act/prefab/save {\"id\":\"...\",\"path\":\"Assets/Prefabs/Foo.prefab\"} (header X-Bridge-Token: <token>)",
                ExampleResponseAbbrev = "{\"tier\":\"act\",\"autoSaved\":true,\"prefab\":{\"guid\":\"...\",\"path\":\"Assets/Prefabs/Foo.prefab\"},\"object\":{\"id\":\"...\",\"name\":\"Foo\",\"active\":true,\"componentNames\":[\"Transform\"],\"components\":[{\"type\":\"Transform\",\"fields\":{...}}]}}",
                TimeoutMs = 5000,
                BuildMutation = Build
            });
        }

        static BridgeHandlerResult Build(Dictionary<string, object> body)
        {
            body = body ?? new Dictionary<string, object>();
            var go = MutationBody.ResolveIdOrThrow(body);

            string path = MutationBody.GetString(body, "path");
            if (string.IsNullOrEmpty(path))
                throw new MutationRejection(400, new Dictionary<string, object> { { "tier", "act" }, { "error", "missing_path" } });

            ValidatePathOrThrow(path);

            if (AssetDatabase.LoadAssetAtPath<Object>(path) != null)
            {
                throw new MutationRejection(409, new Dictionary<string, object> { { "tier", "act" }, { "error", "asset_exists" }, { "path", path } });
            }

            // Covers the scene-side prefab-link change (LOCKED Undo table)
            // — the new asset file itself is not something Undo governs,
            // same as any other AssetDatabase write.
            Undo.RecordObject(go, "Bridge: Save As Prefab");

            // Real bug found live during pre-gate smoke testing, not
            // guessed: SaveAsPrefabAssetAndConnect's return value is the
            // saved PREFAB ASSET's own root GameObject (confirmed against
            // Unity's own scripting reference — "the root GameObject of the
            // saved Prefab Asset"), a DIFFERENT object from the scene
            // instance — not the reconnected `go` a first read of the API
            // name suggests. `go` itself is what gets modified in place
            // into the connected instance, and is what the response must
            // be built from; building from the asset-side return value
            // instead produced a response whose id/name pointed at the
            // prefab asset (identifierType 1) rather than the live scene
            // object /scene/summary actually reports (identifierType 2),
            // and made autoSaved falsely read false (the asset-side
            // object's .scene is never valid). The return value is also
            // documented as sometimes null even on success (batch asset
            // operations, before reimport) — checking it for null would
            // have been a second, separate false-failure risk, so only
            // `success` is checked here.
            PrefabUtility.SaveAsPrefabAssetAndConnect(go, path, InteractionMode.AutomatedAction, out bool success);
            if (!success)
            {
                throw new MutationRejection(500, new Dictionary<string, object> { { "tier", "act" }, { "error", "save_failed" }, { "path", path } });
            }

            string guid = AssetDatabase.AssetPathToGUID(path);
            bool autoSaved = MutationAutoSave.SaveIfEnabled(go);

            var responseBody = new Dictionary<string, object>
            {
                { "tier", "act" }, { "autoSaved", autoSaved },
                { "prefab", new Dictionary<string, object> { { "guid", guid }, { "path", path } } },
                { "object", MutationNodeBuilder.BuildNode(go) }
            };
            return new BridgeHandlerResult { Status = 200, Body = responseBody };
        }

        // Self-protection section (LOCKED): must start under Assets/, must
        // not resolve under the bridge's own package, must end in .prefab.
        // The self-package check is logically subsumed by the Assets/ check
        // (the package lives under Packages/, never Assets/) but kept as its
        // own explicit condition to match the brief's own two-bullet
        // enumeration and acceptance criterion 8's two separately-tested
        // cases.
        static void ValidatePathOrThrow(string path)
        {
            bool startsUnderAssets = path.StartsWith("Assets/", System.StringComparison.Ordinal);
            bool underSelfPackage = path.StartsWith(IndexStore.SelfPackagePrefix, System.StringComparison.Ordinal);
            bool endsInPrefab = path.EndsWith(".prefab", System.StringComparison.Ordinal);

            if (!startsUnderAssets || underSelfPackage || !endsInPrefab)
            {
                throw new MutationRejection(400, new Dictionary<string, object>
                {
                    { "tier", "act" }, { "error", "invalid_path" }, { "path", path }
                });
            }
        }
    }
}
