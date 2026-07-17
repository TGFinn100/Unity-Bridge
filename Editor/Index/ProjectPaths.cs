using System.IO;
using UnityEngine;

namespace UnityBridge.Editor
{
    // Shared Assets-relative <-> absolute path conversion. Centralised here
    // so BridgeServer's port-file write and the Phase 2 scanners agree on
    // what "project root" means instead of each recomputing it.
    internal static class ProjectPaths
    {
        internal static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        internal static string ToAbsolute(string assetPath) => Path.Combine(ProjectRoot, assetPath);

        internal static string LibraryUnityBridgeDir => Path.Combine(ProjectRoot, "Library", "UnityBridge");
    }
}
