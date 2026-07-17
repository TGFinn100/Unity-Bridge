using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;

namespace UnityBridge.Editor
{
    internal static class PlaymodeEndpoint
    {
        static string MarkerPath => Path.Combine(ProjectPaths.LibraryUnityBridgeDir, "playmode_entered");

        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "GET",
                Path = "/playmode",
                TopicKey = "playmode",
                Tier = "live",
                Summary = "Play mode state: playing, paused, time since entry",
                Params = System.Array.Empty<string>(),
                ExampleRequest = "GET /playmode",
                ExampleResponseAbbrev = "{\"tier\":\"live\",\"isPlaying\":true,\"isPaused\":false,\"elapsedSeconds\":12.4,\"frame\":740}",
                TimeoutMs = 5000,
                Handler = Handle
            });
        }

        static Dictionary<string, object> Handle(BridgeRequestContext ctx)
        {
            var body = new Dictionary<string, object>
            {
                { "tier", "live" },
                { "isPlaying", EditorApplication.isPlaying },
                { "isPaused", EditorApplication.isPaused }
            };

            if (EditorApplication.isPlaying)
                body["elapsedSeconds"] = Math.Round(ElapsedSecondsSinceEntry(), 1);

            BridgeState.AddFrameIfPlaying(body);
            return body;
        }

        // Reads the persisted entry marker directly rather than caching the
        // timestamp in a static field, so it's correct regardless of
        // whether a domain reload happened between play-mode entry and this
        // request (the common case), whether Configurable Enter Play Mode
        // skipped the reload entirely, or whether this process's static
        // state was rebuilt mid-session by an unrelated script recompile.
        static double ElapsedSecondsSinceEntry()
        {
            if (File.Exists(MarkerPath))
            {
                string text = File.ReadAllText(MarkerPath);
                if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var entered))
                    return (DateTime.UtcNow - entered).TotalSeconds;
            }

            // Marker missing while already playing (e.g. the very first
            // request after some unusual startup ordering) — not brief-
            // specified; write it now, best-effort, and report 0 rather
            // than erroring.
            WriteMarker();
            return 0;
        }

        // Hooked from BridgeServer's static ctor. Fires on the main thread,
        // same as the Tick() pump that answers /playmode requests, so no
        // locking is needed between the write here and the read above.
        internal static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode) WriteMarker();
            else if (change == PlayModeStateChange.EnteredEditMode) DeleteMarker();
        }

        static void WriteMarker()
        {
            string dir = ProjectPaths.LibraryUnityBridgeDir;
            Directory.CreateDirectory(dir);
            string tempPath = MarkerPath + ".tmp";
            File.WriteAllText(tempPath, DateTime.UtcNow.ToString("o"));
            if (File.Exists(MarkerPath)) File.Replace(tempPath, MarkerPath, null);
            else File.Move(tempPath, MarkerPath);
        }

        static void DeleteMarker()
        {
            try { File.Delete(MarkerPath); } catch { /* best-effort cleanup only */ }
        }
    }
}
