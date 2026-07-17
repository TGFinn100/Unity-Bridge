using System.Collections.Generic;
using UnityEditor;

namespace UnityBridge.Editor
{
    // Stages AssetPostprocessor batches instead of applying them immediately.
    // Debounced (per the LOCKED "debounce full-directory moves" note):
    // a single user action (e.g. moving a folder) can trigger this callback
    // more than once in quick succession, so FlushIfDue() — called from
    // BridgeServer.Tick(), same main-thread dispatcher as everything else —
    // only commits once ~250ms has passed since the last touch.
    internal sealed class BridgeAssetPostprocessor : AssetPostprocessor
    {
        const double DebounceSeconds = 0.25;

        static readonly HashSet<string> _pendingTouched = new HashSet<string>();
        static readonly HashSet<string> _pendingDeleted = new HashSet<string>();
        static double _lastTouchTime = -1;

        static void OnPostprocessAllAssets(
            string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            foreach (var p in importedAssets) _pendingTouched.Add(p);
            foreach (var p in movedAssets) _pendingTouched.Add(p);
            foreach (var p in deletedAssets) _pendingDeleted.Add(p);
            foreach (var p in movedFromAssetPaths) _pendingDeleted.Add(p);

            if (_pendingTouched.Count > 0 || _pendingDeleted.Count > 0)
                _lastTouchTime = EditorApplication.timeSinceStartup;
        }

        internal static void FlushIfDue()
        {
            if (_lastTouchTime < 0) return;
            if (_pendingTouched.Count == 0 && _pendingDeleted.Count == 0) return;
            if (EditorApplication.timeSinceStartup - _lastTouchTime < DebounceSeconds) return;

            var touched = new HashSet<string>(_pendingTouched);
            var deleted = new HashSet<string>(_pendingDeleted);
            _pendingTouched.Clear();
            _pendingDeleted.Clear();
            _lastTouchTime = -1;

            IndexStore.ApplyIncrementalUpdate(touched, deleted);
        }
    }
}
