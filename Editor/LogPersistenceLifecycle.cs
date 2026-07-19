using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityBridge.Editor
{
    // v1.6: a reliable signal for "this reload was caused by entering Play
    // Mode" — needed by LogBuffer.Load()/LogWatchStore.Load() to decide
    // wipe-vs-load at static-ctor time, before any Tick() has run.
    //
    // EditorApplication.isPlaying is NOT a reliable signal for this, despite
    // the original v1.6 brief's mechanism assuming it was (LOCKED text:
    // "isPlaying == true at that moment [the static ctor] -> this reload
    // was caused by entering Play Mode"). Disproved by live testing
    // 2026-07-19: the wipe never fired on any Play Mode entry in the
    // sandbox project — a watch registered before entry still had its
    // oldest record's timestamp predate the /act/playmode/enter request by
    // over 30 seconds, immediately after a confirmed transition to
    // readyState:"playmode". isPlaying apparently doesn't read back true
    // until sometime after this reload's static constructors have already
    // run, making it indistinguishable at that moment from a plain
    // recompile or an exit-triggered reload (both also read isPlaying as
    // false at ctor time). See package README DECISIONS for the full
    // incident.
    //
    // Fix: the same event-driven marker pattern PlaymodeEndpoint already
    // uses for its own entry-timing marker. PlayModeStateChange.
    // ExitingEditMode fires synchronously BEFORE the domain reload that
    // follows an entry into Play Mode, so writing a marker file there and
    // consuming (deleting) it during the very next Load() reliably survives
    // exactly one reload — the one caused by entering Play Mode — and no
    // other; a marker written for entry is gone before any later plain
    // recompile or exit reload could ever see it.
    internal static class LogPersistenceLifecycle
    {
        static string MarkerPath => Path.Combine(ProjectPaths.LibraryUnityBridgeDir, "entering_playmode");

        // Hooked from BridgeServer's static ctor, same as
        // PlaymodeEndpoint.OnPlayModeStateChanged — both subscribe to the
        // same event independently.
        internal static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingEditMode) return;

            try
            {
                Directory.CreateDirectory(ProjectPaths.LibraryUnityBridgeDir);
                File.WriteAllText(MarkerPath, "");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityBridge] Failed to write Play Mode entry marker: {ex.Message}");
            }
        }

        // Pure file I/O — safe to call from BridgeServer's static
        // constructor before any Tick() has run. Consumes (deletes) the
        // marker so it only ever causes a wipe for the one reload it was
        // written for.
        //
        // Call this EXACTLY ONCE per static-ctor run (from BridgeServer,
        // not from LogBuffer/LogWatchStore individually) and pass the
        // result to both — a real bug caught during v1.6 acceptance testing
        // 2026-07-19: each store originally called this itself, so whichever
        // ran first deleted the marker and the second always saw "no
        // marker," silently skipping its own wipe. Confirmed live: the
        // watch store wiped correctly but the raw log buffer didn't, on the
        // same Play Mode entry.
        internal static bool ConsumeEnteringPlayModeMarker()
        {
            if (!File.Exists(MarkerPath)) return false;
            try { File.Delete(MarkerPath); } catch (Exception ex) { Debug.LogWarning($"[UnityBridge] Failed to delete Play Mode entry marker: {ex.Message}"); }
            return true;
        }
    }
}
